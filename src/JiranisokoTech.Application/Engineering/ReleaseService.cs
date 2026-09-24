using System.Text;
using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.Engineering;

namespace JiranisokoTech.Application.Engineering;

/// <summary>One commit, as a changelog needs it.</summary>
/// <remarks>
/// A projection rather than the <see cref="Commit"/> entity, because the work item's number and
/// title come from another aggregate and the alternative is loading every work item a release
/// touched in order to read two fields off each.
/// </remarks>
public sealed record ChangelogEntry(
    string Sha,
    string Message,
    string Author,
    DateTimeOffset At,
    int? WorkItemNumber,
    string? WorkItemTitle);

/// <summary>What releases need read and written.</summary>
public interface IReleaseRepository
{
    Task<Release?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every release of one repository, in no particular order.
    /// </summary>
    /// <remarks>
    /// All of them, and ordered afterwards in memory rather than in SQL. Two reasons, and the
    /// second is the real one: a repository has releases in the tens rather than the millions,
    /// and the ordering a release list needs cannot be expressed in SQL at all — prerelease
    /// tails compare identifier by identifier, so <c>rc.9</c> before <c>rc.10</c> is a rule no
    /// ORDER BY on a text column obeys. Sorting in the database would be faster and wrong.
    /// </remarks>
    Task<List<Release>> ForRepositoryAsync(
        Guid repositoryId, CancellationToken cancellationToken = default);

    /// <summary>When the commit a release names was made, if it is recorded here.</summary>
    Task<DateTimeOffset?> CommitAtAsync(
        Guid repositoryId, string sha, CancellationToken cancellationToken = default);

    /// <summary>
    /// The commits in a window, newest first, with the work each belongs to.
    /// </summary>
    /// <remarks>
    /// Bounded by <paramref name="most"/> because the first release of a repository that has
    /// been recording commits for a year would otherwise draft a changelog with four thousand
    /// lines in it, and whoever asked for it would wait while the page built something they
    /// were going to delete.
    /// </remarks>
    Task<List<ChangelogEntry>> CommitsBetweenAsync(
        Guid repositoryId,
        DateTimeOffset? after,
        DateTimeOffset until,
        int most,
        CancellationToken cancellationToken = default);

    void Add(Release release);

    Task SaveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A drafted changelog, and what is wrong with it.
/// </summary>
/// <remarks>
/// The caveat travels with the text rather than being logged or swallowed, because every way
/// this draft can be incomplete is invisible in the result: a window with no commits in it and
/// a window that could not be worked out both produce an empty changelog, and they want
/// different responses from the person reading.
/// </remarks>
public sealed record NotesDraft(string Text, int Commits, string? Caveat);

/// <summary>
/// One repository's releases, in order, and which one is out.
/// </summary>
public sealed record ReleaseHistory(
    Guid RepositoryId, IReadOnlyList<Release> Releases, Release? Live)
{
    public bool IsEmpty => Releases.Count == 0;
}

/// <summary>
/// Naming versions, writing what is in them, and withdrawing the ones that failed.
/// </summary>
/// <remarks>
/// Section 67. The whole of what this adds over the four hosts already reporting into the
/// system is a name and a sentence — which is the part no host can supply, because neither is a
/// fact about a pipeline.
///
/// <b>The changelog is drafted from commits and then edited.</b> Not generated and used as-is:
/// a commit message is written for the next developer and a changelog is read by everybody
/// else, and a release note that says "fix off-by-one in the loop" tells a client nothing. The
/// draft exists so that nobody has to reconstruct a fortnight from memory, and the edit exists
/// because the draft is raw material.
///
/// <b>The window is measured in time, not in ancestry, and the page says so.</b> This system
/// records commits as they are pushed; it does not hold the commit graph, so it cannot answer
/// "what is reachable from this sha and not from that one" — the question a real changelog tool
/// asks its clone of the repository. What it can answer is "what was pushed to this repository
/// between these two commits", and for a firm releasing from one mainline those are the same
/// set. They come apart when a long-lived branch is merged: its commits were pushed weeks ago
/// and land in an earlier release's window than the one they went out in. Guessing harder would
/// mean cloning repositories and running git, which is a different system; claiming ancestry we
/// do not have would mean a changelog quietly missing the work somebody did on a branch.
/// </remarks>
public sealed class ReleaseService(IReleaseRepository releases, IClock clock)
{
    /// <summary>The most commits a drafted changelog will read.</summary>
    /// <remarks>
    /// Said out loud in the caveat when it bites, because a silently truncated changelog is
    /// indistinguishable from a complete one and would be published as though it were.
    /// </remarks>
    public const int MostCommits = 300;

    /// <summary>How many recent commits the release form offers to choose from.</summary>
    /// <remarks>
    /// Thirty, because a release is nearly always of something pushed in the last few days and
    /// a list long enough to scroll is a list somebody stops reading. Anything older is reached
    /// by releasing from the host and recording the version afterwards.
    /// </remarks>
    public const int MostToPickFrom = 30;

    /// <summary>
    /// Name a version, before anybody claims it is out.
    /// </summary>
    /// <remarks>
    /// The version is checked against what already exists here rather than left to the unique
    /// index. Both would refuse it, but only this one can say "1.4.0 was rolled back on 3 March"
    /// — and a person who typed a version that has been used before nearly always wants to know
    /// which release used it rather than to be told again that they cannot.
    /// </remarks>
    public async Task<Release> PrepareAsync(
        Guid repositoryId,
        string version,
        string sha,
        Guid by,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        var numbered = ReleaseVersion.Parse(version);

        var already = await Taken(repositoryId, numbered, cancellationToken);

        if (already is not null)
        {
            throw new InvalidOperationException(
                $"{numbered} already exists here — it {Say(already.Status)}. Pick another "
                + "version, or abandon that one first if it was never used.");
        }

        var release = Release.Prepare(repositoryId, numbered, sha, by, clock.Now, name);

        releases.Add(release);
        await releases.SaveAsync(cancellationToken);

        return release;
    }

    /// <summary>
    /// Assemble what changed since the last release, for somebody to edit.
    /// </summary>
    /// <remarks>
    /// Writes nothing. A draft that saved itself would overwrite notes somebody had already
    /// written the moment they pressed the button a second time.
    /// </remarks>
    public async Task<NotesDraft> DraftNotesAsync(
        Guid releaseId, CancellationToken cancellationToken = default)
    {
        var release = await Required(releaseId, cancellationToken);
        var all = await releases.ForRepositoryAsync(release.RepositoryId, cancellationToken);

        var until = await releases.CommitAtAsync(
            release.RepositoryId, release.Sha, cancellationToken);

        if (until is null)
        {
            return new NotesDraft(
                string.Empty,
                0,
                "The commit this release names has not been recorded here, so there is nothing "
                + "to draft from. Either the repository is not connected to a host that sends "
                + "pushes, or that commit was never pushed to it.");
        }

        var previous = Before(all, release);

        var after = previous is null
            ? null
            : await releases.CommitAtAsync(release.RepositoryId, previous.Sha, cancellationToken);

        var commits = await releases.CommitsBetweenAsync(
            release.RepositoryId, after, until.Value, MostCommits + 1, cancellationToken);

        var truncated = commits.Count > MostCommits;

        if (truncated)
        {
            commits = commits.Take(MostCommits).ToList();
        }

        return new NotesDraft(
            Compose(commits),
            commits.Count,
            Caveat(previous, after, truncated, commits.Count));
    }

    /// <summary>
    /// The last commits pushed to a repository, to pick a release's commit from.
    /// </summary>
    /// <remarks>
    /// So that nobody types a sha. Forty characters of hex copied from another window is the
    /// most likely thing on the release form to be wrong, and a release pointing at a commit
    /// that is off by one character produces an empty changelog and a rollback instruction
    /// nobody can follow. The commits are already here; choosing from them cannot be mistyped.
    /// </remarks>
    public Task<List<ChangelogEntry>> RecentCommitsAsync(
        Guid repositoryId, CancellationToken cancellationToken = default) =>
        releases.CommitsBetweenAsync(
            repositoryId, null, DateTimeOffset.MaxValue, MostToPickFrom, cancellationToken);

    /// <summary>Replace the notes of a release that is still being prepared.</summary>
    public async Task WriteAsync(
        Guid releaseId, string notes, CancellationToken cancellationToken = default)
    {
        var release = await Required(releaseId, cancellationToken);

        release.Write(notes);

        await releases.SaveAsync(cancellationToken);
    }

    /// <summary>Add a line to the notes of a release that is already out.</summary>
    public async Task AmendAsync(
        Guid releaseId, string addition, CancellationToken cancellationToken = default)
    {
        var release = await Required(releaseId, cancellationToken);

        release.Amend(addition, clock.Now);

        await releases.SaveAsync(cancellationToken);
    }

    /// <summary>Correct the version or the name of a release that has not gone out.</summary>
    public async Task RenameAsync(
        Guid releaseId,
        string version,
        string? name,
        CancellationToken cancellationToken = default)
    {
        var release = await Required(releaseId, cancellationToken);
        var numbered = ReleaseVersion.Parse(version);

        if (numbered != release.Version)
        {
            var already = await Taken(release.RepositoryId, numbered, cancellationToken);

            if (already is not null)
            {
                throw new InvalidOperationException(
                    $"{numbered} already exists here — it {Say(already.Status)}.");
            }
        }

        release.Rename(numbered, name);

        await releases.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Say that a version is what the firm is running.
    /// </summary>
    /// <remarks>
    /// Nothing here refuses a version lower than the one already out. Shipping 1.3.3 while
    /// 1.4.0 is live is a patch to an older line, which is a normal thing to do and not
    /// something a system should argue with — the release list says which is the highest and
    /// which was declared most recently, and lets a person read the difference.
    /// </remarks>
    public async Task DeclareAsync(
        Guid releaseId, Guid by, CancellationToken cancellationToken = default)
    {
        var release = await Required(releaseId, cancellationToken);

        release.Declare(by, clock.Now);

        await releases.SaveAsync(cancellationToken);
    }

    /// <summary>Withdraw a version that went out.</summary>
    public async Task RollBackAsync(
        Guid releaseId, Guid by, string why, CancellationToken cancellationToken = default)
    {
        var release = await Required(releaseId, cancellationToken);

        release.RollBack(by, why, clock.Now);

        await releases.SaveAsync(cancellationToken);
    }

    /// <summary>Drop a version that was prepared and never declared.</summary>
    public async Task AbandonAsync(
        Guid releaseId, string why, CancellationToken cancellationToken = default)
    {
        var release = await Required(releaseId, cancellationToken);

        release.Abandon(why);

        await releases.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// One repository's releases, highest version first, and which one is out.
    /// </summary>
    /// <remarks>
    /// <b>Live is the highest version still released, not the most recent one declared.</b>
    /// Those differ exactly when a patch to an older line goes out after a newer release —
    /// declaring 1.3.3 on Tuesday does not make the firm stop running 1.4.0 — and a screen
    /// answering "what are we on" has to pick the one that is true.
    /// </remarks>
    public async Task<ReleaseHistory> HistoryAsync(
        Guid repositoryId, CancellationToken cancellationToken = default)
    {
        var all = await releases.ForRepositoryAsync(repositoryId, cancellationToken);
        var ordered = Ordered(all);

        return new ReleaseHistory(
            repositoryId, ordered, ordered.FirstOrDefault(one => one.IsOut));
    }

    /// <summary>Highest version first, with the most recently prepared breaking ties.</summary>
    public static List<Release> Ordered(IEnumerable<Release> all) =>
        [.. all
            .OrderByDescending(one => one.Version)
            .ThenByDescending(one => one.PreparedAt)];

    /// <summary>
    /// The release this one follows, for working out the window.
    /// </summary>
    /// <remarks>
    /// The highest version below this one that actually went out. A prepared release is skipped
    /// because it may never go out, and an abandoned one is skipped because it did not — in
    /// either case its commit is not a boundary anybody was told about, and using it as one
    /// would silently cut the top off the changelog.
    /// </remarks>
    private static Release? Before(IEnumerable<Release> all, Release release) =>
        Ordered(all.Where(one => one.WasOut && one.Version < release.Version))
            .FirstOrDefault();

    private async Task<Release?> Taken(
        Guid repositoryId, ReleaseVersion version, CancellationToken cancellationToken)
    {
        var all = await releases.ForRepositoryAsync(repositoryId, cancellationToken);

        return all.FirstOrDefault(one =>
            one.Status != ReleaseStatus.Abandoned && one.Version == version);
    }

    /// <summary>
    /// The commits, as lines somebody can edit down.
    /// </summary>
    /// <remarks>
    /// Grouped by the work item each commit belongs to, because that is the level a changelog
    /// is read at — six commits fixing one thing are one line about one thing, and the work
    /// item already carries the sentence somebody wrote when they raised it.
    ///
    /// Merges are dropped. "Merge pull request #41 from feature/x" is the one message in a
    /// repository guaranteed to say nothing about what changed, and leaving them in means the
    /// first thing anybody does with every draft is delete half of it.
    ///
    /// Only the first line of each message is used, for the same reason: the body of a commit
    /// message is addressed to whoever reviews the diff.
    /// </remarks>
    private static string Compose(IReadOnlyList<ChangelogEntry> commits)
    {
        var useful = commits
            .Where(one => !IsMerge(one.Message))
            .ToList();

        if (useful.Count == 0)
        {
            return string.Empty;
        }

        var text = new StringBuilder();

        var byWork = useful
            .Where(one => one.WorkItemNumber is not null)
            .GroupBy(one => (one.WorkItemNumber!.Value, one.WorkItemTitle))
            .OrderBy(group => group.Key.Item1);

        foreach (var group in byWork)
        {
            text.Append("- #").Append(group.Key.Item1);

            if (group.Key.WorkItemTitle is { Length: > 0 } title)
            {
                text.Append(' ').Append(title);
            }

            text.AppendLine();

            foreach (var line in Subjects(group))
            {
                text.Append("  - ").AppendLine(line);
            }
        }

        var loose = useful.Where(one => one.WorkItemNumber is null).ToList();

        if (loose.Count > 0)
        {
            if (text.Length > 0)
            {
                text.AppendLine();
                text.AppendLine("Not attached to any work item:");
            }

            foreach (var line in Subjects(loose))
            {
                text.Append("- ").AppendLine(line);
            }
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// The first lines, oldest first and each said once.
    /// </summary>
    /// <remarks>
    /// Oldest first inside a group, because a changelog reads as a story and the commits come
    /// back newest first. Deduplicated because a cherry-picked fix appears under two shas with
    /// the same subject, and a changelog that lists it twice looks like it happened twice.
    /// </remarks>
    private static IEnumerable<string> Subjects(IEnumerable<ChangelogEntry> commits) =>
        commits
            .OrderBy(one => one.At)
            .Select(one => Subject(one.Message))
            .Where(one => one.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static string Subject(string message)
    {
        var end = message.IndexOfAny(['\r', '\n']);

        return (end < 0 ? message : message[..end]).Trim();
    }

    private static bool IsMerge(string message) =>
        message.StartsWith("Merge ", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What the reader has to know about the draft in front of them.
    /// </summary>
    /// <remarks>
    /// The first release of a repository is the case worth naming: with nothing before it the
    /// window has no floor, so what comes back is every commit ever pushed, which is not a
    /// changelog for a first release — it is the history of the project. Better to say so than
    /// to let somebody publish it.
    /// </remarks>
    private static string? Caveat(
        Release? previous, DateTimeOffset? after, bool truncated, int found)
    {
        var said = new List<string>();

        if (previous is null)
        {
            said.Add(
                "Nothing has been released here before, so this is everything pushed up to "
                + "that commit rather than what changed since a previous version.");
        }
        else if (after is null)
        {
            said.Add(
                $"The commit {previous.Version} named is not recorded here, so the window has "
                + "no start and this is everything pushed up to that commit.");
        }
        else
        {
            said.Add(
                $"Commits pushed between {previous.Version} and this one. The window is "
                + "measured in time rather than in ancestry — this system records pushes, not "
                + "the commit graph — so work merged from a long-lived branch may sit in an "
                + "earlier release's window than the one it went out in.");
        }

        if (truncated)
        {
            said.Add(
                $"Only the first {MostCommits} commits were read. There are more in the "
                + "window than that.");
        }

        if (found == 0)
        {
            said.Add("No commits were found in it.");
        }

        return said.Count == 0 ? null : string.Join(" ", said);
    }

    private static string Say(ReleaseStatus status) => status switch
    {
        ReleaseStatus.Prepared => "is being prepared",
        ReleaseStatus.Released => "is out",
        ReleaseStatus.RolledBack => "was rolled back",
        _ => "was abandoned",
    };

    private async Task<Release> Required(Guid id, CancellationToken cancellationToken) =>
        await releases.FindAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("That release no longer exists.");
}
