using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Engineering;

/// <summary>
/// Where a release has got to.
/// </summary>
/// <remarks>
/// Four, and the two at the ends are the ones that carry the weight.
/// <see cref="Prepared"/> is a version somebody is writing the notes for and has not claimed
/// yet; <see cref="RolledBack"/> is the admission that what was claimed had to be withdrawn.
/// </remarks>
public enum ReleaseStatus
{
    /// <summary>Named, with notes being written. Nothing is claimed yet.</summary>
    Prepared = 1,

    /// <summary>This is what the firm is running.</summary>
    Released = 2,

    /// <summary>It went out and had to come back.</summary>
    RolledBack = 3,

    /// <summary>Prepared and never declared, with a reason.</summary>
    Abandoned = 4,
}

/// <summary>
/// A version of something the firm can name.
/// </summary>
/// <remarks>
/// Section 67, and the last link in section 91's chain — the one step between "a deployment
/// happened" and "we shipped 1.4.0". Everything either side of it already existed: commits and
/// pull requests and reviews arrive from four hosts, builds and deployments are recorded, the
/// environments screen says what is running where, and profitability comes out at the end.
/// What was missing was the only part of that chain a person says rather than a host reports.
///
/// <b>A release is declared, not inferred from a deployment.</b> This is the decision the rest
/// of the section hangs on, and it is the same one <see cref="Deployment"/> makes in the other
/// direction. Deployments arrive constantly and most of them are nothing: a staging push, a
/// retry, somebody's branch. Inferring a release from the newest successful production
/// deployment would mean an accidental deploy of a stale branch renames what the firm is
/// running, and it would mean the firm cannot say "that went out but we are not calling it
/// 1.4.0 yet" — which is the normal state of affairs for a day or two.
///
/// <b>It records, and it runs nothing.</b> Rolling back here writes down that a version was
/// withdrawn; it does not redeploy anything, because nothing in this system runs a pipeline.
/// The same refusal as the promote button that section 13 declined: a control that looks like
/// it acts and does not is worse than no control, and the screen says so in as many words.
///
/// <b>The commit is the anchor.</b> A release names a sha, which is what lets the changelog be
/// assembled from commits already recorded and what makes "roll back to 1.3.2" a sentence
/// somebody can act on — they have the commit. A release with a version and no commit would be
/// a label with nothing under it.
/// </remarks>
public sealed class Release : Entity, IAuditable
{
    private Release()
    {
        Sha = string.Empty;
        Notes = string.Empty;
        Number = string.Empty;
    }

    private Release(
        Guid repositoryId,
        ReleaseVersion version,
        string sha,
        string? name,
        Guid preparedBy,
        DateTimeOffset at)
    {
        RepositoryId = repositoryId;
        Version = version;
        Sha = Hash(sha);
        Name = Trimmed(name);
        Notes = string.Empty;
        Status = ReleaseStatus.Prepared;
        PreparedById = preparedBy;
        PreparedAt = at;
    }

    /// <summary>
    /// Name a version and start writing what is in it.
    /// </summary>
    /// <remarks>
    /// Preparing and declaring are two steps for the same reason drafting and approving a pay
    /// run are: the notes are the part that takes a person half an hour, and a release that
    /// existed only at the moment it was claimed would be one that always went out with an
    /// empty changelog.
    /// </remarks>
    public static Release Prepare(
        Guid repositoryId,
        ReleaseVersion version,
        string sha,
        Guid preparedBy,
        DateTimeOffset at,
        string? name = null) =>
        new(repositoryId, version, sha, name, preparedBy, at);

    public Guid RepositoryId { get; private init; }

    /// <summary>
    /// The version, as something orderable rather than as text.
    /// </summary>
    /// <remarks>
    /// Kept as its parts underneath and assembled here, rather than mapped as a complex property
    /// the way an employee's terms are. The reason is indexing: a unique index cannot be written
    /// over the members of a complex type, and the constraint that stops two live 1.4.0s from
    /// existing has to be an index rather than a check in application code, which two people
    /// pressing a button in the same minute both pass. The parts are also what makes the version
    /// readable in the database and orderable in a query, neither of which a packed string is.
    ///
    /// The column the index actually uses is <see cref="Number"/>, for a reason worth reading
    /// there before touching any of this.
    /// </remarks>
    public ReleaseVersion Version
    {
        get => new(Major, Minor, Patch, Prerelease);

        private set
        {
            Major = value.Major;
            Minor = value.Minor;
            Patch = value.Patch;
            Prerelease = value.Prerelease;
            Number = value.ToString();
        }
    }

    /// <summary>
    /// The version written out, which is the column the uniqueness is enforced on.
    /// </summary>
    /// <remarks>
    /// <b>A derived column that exists because of how SQL treats null.</b> The obvious index for
    /// "one release per version per repository" is over the four parts, and it was written that
    /// way first — and it enforced nothing at all for an ordinary release. Nulls are distinct in
    /// a unique index, every version without a prerelease tail stores null in that column, and so
    /// two rows both claiming 1.4.0 satisfied it. A test that inserted exactly that pair and
    /// expected a refusal is what caught it; nothing else would have, because the failure is two
    /// plausible rows rather than an error.
    ///
    /// Writing the version out gives the index a column that is never null. It is duplication,
    /// and it is worth it: the alternatives are a constraint that only holds for release
    /// candidates, or a check in application code that two people pressing a button in the same
    /// minute both pass.
    /// </remarks>
    public string Number { get; private set; }

    /// <summary>The first number, stored so that the database can order and constrain it.</summary>
    public int Major { get; private set; }

    public int Minor { get; private set; }

    public int Patch { get; private set; }

    /// <summary>The prerelease tail, null when this is not a candidate.</summary>
    public string? Prerelease { get; private set; }

    /// <summary>The commit this release is of.</summary>
    public string Sha { get; private init; }

    /// <summary>What it is called, when a number is not enough.</summary>
    /// <remarks>
    /// Optional, because most releases are a number and nothing else. A firm that names its
    /// releases gets to; one that does not is not made to type something.
    /// </remarks>
    public string? Name { get; private set; }

    /// <summary>
    /// What changed, in prose.
    /// </summary>
    /// <remarks>
    /// Plain text, deliberately. A changelog is read by whoever is deciding whether to upgrade
    /// and by whoever is working out what broke, and both of them are better served by lines
    /// they can scan than by a rich document that renders differently in three places. It is
    /// drafted from the commits between this release and the last one — see the note on the
    /// application service about why that window is measured in time rather than in ancestry —
    /// and then edited by a person, because a commit message is written for the next developer
    /// and a changelog is written for everybody else.
    /// </remarks>
    public string Notes { get; private set; }

    public ReleaseStatus Status { get; private set; }

    public Guid PreparedById { get; private init; }

    public DateTimeOffset PreparedAt { get; private init; }

    public Guid? DeclaredById { get; private set; }

    public DateTimeOffset? DeclaredAt { get; private set; }

    public Guid? EndedById { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    /// <summary>Why it was rolled back, or why it was never declared.</summary>
    /// <remarks>
    /// One field for both because a reader is asking the same question either way — what
    /// happened to this version — and two nullable columns holding a sentence each would be
    /// two places for it to be missing from.
    /// </remarks>
    public string? Outcome { get; private set; }

    /// <summary>Is this the version the firm is claiming to run?</summary>
    public bool IsOut => Status == ReleaseStatus.Released;

    /// <summary>Was it declared at some point, whatever happened afterwards?</summary>
    /// <remarks>
    /// Separate from <see cref="IsOut"/> because a rolled-back release did go out, and
    /// anybody counting what the firm shipped this quarter has to count it.
    /// </remarks>
    public bool WasOut => Status is ReleaseStatus.Released or ReleaseStatus.RolledBack;

    /// <summary>Correct the version or the name before it is declared.</summary>
    /// <remarks>
    /// Only while prepared. After it is out the version is what people have been told they are
    /// running, and renaming it would make every reference to it wrong at once.
    /// </remarks>
    public void Rename(ReleaseVersion version, string? name)
    {
        RefuseUnless(ReleaseStatus.Prepared, "renamed");

        Version = version;
        Name = Trimmed(name);
    }

    /// <summary>Replace the notes while they are still being written.</summary>
    public void Write(string notes)
    {
        RefuseUnless(ReleaseStatus.Prepared, "rewritten");

        Notes = notes?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// Add to the notes after the fact, without rewriting them.
    /// </summary>
    /// <remarks>
    /// Appended and dated rather than replaced, and that is the whole point of having a second
    /// method. Once a version is out, its notes are what the firm told people went out; a
    /// system where that record can be quietly rewritten cannot answer "what changed between
    /// March and April" a year later. Something left out gets added underneath, where it is
    /// visibly an afterthought, which is what it is.
    /// </remarks>
    public void Amend(string addition, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(addition))
        {
            throw new ArgumentException("There is nothing to add.", nameof(addition));
        }

        if (Status == ReleaseStatus.Abandoned)
        {
            throw new InvalidOperationException(
                "This release was abandoned, so there are no notes to add to.");
        }

        var line = $"Added {at:d MMM yyyy}: {addition.Trim()}";

        Notes = Notes.Length == 0 ? line : $"{Notes}\n\n{line}";
    }

    /// <summary>
    /// Say that this version is what the firm is running.
    /// </summary>
    public void Declare(Guid by, DateTimeOffset at)
    {
        if (Status == ReleaseStatus.Released)
        {
            return;
        }

        RefuseUnless(ReleaseStatus.Prepared, "declared");

        Status = ReleaseStatus.Released;
        DeclaredById = by;
        DeclaredAt = at;

        Raise(new ReleaseDeclared(Id, RepositoryId, Version.ToString(), Sha, at));
    }

    /// <summary>
    /// Withdraw a version that went out, with the reason.
    /// </summary>
    /// <remarks>
    /// The reason is required, and it is the most useful field on this whole aggregate. A list
    /// of versions tells somebody what the firm shipped; a list of versions with "1.4.0 —
    /// withdrawn, the invoice PDF came out blank for clients in USD" tells them what the firm
    /// learned, and that is what anybody reads a release history for.
    /// </remarks>
    public void RollBack(Guid by, string why, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(why))
        {
            throw new ArgumentException(
                "Say why it was rolled back. A withdrawn version with no reason is the one "
                + "thing a release history cannot be read without.",
                nameof(why));
        }

        RefuseUnless(ReleaseStatus.Released, "rolled back");

        Status = ReleaseStatus.RolledBack;
        EndedById = by;
        EndedAt = at;
        Outcome = why.Trim();

        Raise(new ReleaseRolledBack(Id, RepositoryId, Version.ToString(), Sha, Outcome, at));
    }

    /// <summary>
    /// Give up on a version that was prepared and never went out.
    /// </summary>
    /// <remarks>
    /// Kept rather than deleted, for the reason an abandoned pay run is kept: somebody asking
    /// next year why 1.4.0 does not exist finds the row saying it was cut and why. It also
    /// frees the version number, because the uniqueness that stops two live 1.4.0s deliberately
    /// ignores abandoned ones.
    /// </remarks>
    public void Abandon(string why)
    {
        if (string.IsNullOrWhiteSpace(why))
        {
            throw new ArgumentException("Say why it was dropped.", nameof(why));
        }

        RefuseUnless(ReleaseStatus.Prepared, "abandoned");

        Status = ReleaseStatus.Abandoned;
        Outcome = why.Trim();
    }

    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private void RefuseUnless(ReleaseStatus required, string verb)
    {
        if (Status == required)
        {
            return;
        }

        var said = Status switch
        {
            ReleaseStatus.Prepared => "is still being prepared",
            ReleaseStatus.Released => "is already out",
            ReleaseStatus.RolledBack => "has been rolled back",
            _ => "was abandoned",
        };

        throw new InvalidOperationException($"This release {said}, so it cannot be {verb}.");
    }

    /// <summary>
    /// A commit hash, lower-cased, for the reason a deployment's is.
    /// </summary>
    /// <remarks>
    /// This is the side of the join that bends — see <see cref="Deployment"/>. A release whose
    /// sha differs from the recorded commit only in case would show an empty changelog and
    /// nothing anywhere would say why.
    /// </remarks>
    private static string Hash(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException(
                "A release has to name the commit it is of.", nameof(value))
            : value.Trim().ToLowerInvariant();

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// A version is out.
/// </summary>
/// <remarks>
/// Carries the version as text rather than as the value type, because an event is serialised
/// into the outbox and read back by whatever is listening months later — a shape that can be
/// read without the domain assembly agreeing about it is the one worth keeping.
///
/// Nothing in this system handles it yet, and that is a decision rather than an omission. The
/// obvious reactions — telling a client their fix is out, closing the work items in the
/// changelog — are both things a person should do, for the reason a deployment does not move a
/// task.
/// </remarks>
public sealed record ReleaseDeclared(
    Guid ReleaseId,
    Guid RepositoryId,
    string Version,
    string Sha,
    DateTimeOffset At) : DomainEvent;

public sealed record ReleaseRolledBack(
    Guid ReleaseId,
    Guid RepositoryId,
    string Version,
    string Sha,
    string Why,
    DateTimeOffset At) : DomainEvent;
