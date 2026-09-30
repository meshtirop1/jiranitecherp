using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.Audit;
using JiranisokoTech.Infrastructure.Engineering;
using JiranisokoTech.Infrastructure.Work;

namespace JiranisokoTech.Web;

/// <summary>
/// One dated sentence in a stream of what happened to something.
/// </summary>
/// <remarks>
/// <c>Said</c> is a finished sentence rather than a template with holes in it, because the thing
/// section 71 objects to is a list of column changes: "Status: InProgress → InReview" is a row out
/// of a database, and "Moved to in review" is what happened. The wording is decided where the fact
/// is read, once, and the component that draws this knows nothing about work items.
/// </remarks>
/// <param name="Tone">A <c>pill--*</c> class, or null for a line that needs no chip.</param>
/// <param name="ToneSaid">
/// The word inside that chip.
/// </param>
/// <remarks>
/// The word travels with the colour rather than being derived from it, and the first version of
/// this record did derive it. A chip is decoration that repeats the sentence beside it, so one
/// word per pill class looked like enough — until <c>pill--quiet</c>, which is worn by a cancelled
/// work item, a pull request closed without merging and a cancelled build. No single word is true
/// of all three, and the one chosen described two of them and mislabelled the third. Deciding it
/// where the fact is read costs a field and removes the guess.
///
/// An empty chip is not the alternative it looks like: <c>.pill</c> has no minimum width, so a
/// pill with nothing in it renders as a small coloured dot.
/// </remarks>
public sealed record StreamLine(
    DateTimeOffset At,
    string Said,
    string? Tone = null,
    string? ToneSaid = null,
    string? Link = null,
    string? LinkSaid = null);

/// <summary>
/// What happened to one work item, gathered from the places it is already recorded.
/// </summary>
/// <remarks>
/// <b>Section 71 asks for an activity timeline and shows what it means by one:</b> lines like
/// "13:32 Task moved to QA" and "13:58 Sarah approved #182". Every fact behind those lines was
/// already stored in this database before this file existed, in five different places — the work
/// item's own timestamps, its comments, its acceptance criteria, what the repositories reported, and
/// the append-only change trail. Nothing could put them in one order, so the page showed four
/// tables and the reader did the joining.
///
/// <b>There is deliberately no event table.</b> Adding one would mean two records of one move, and
/// the day they disagree the one nobody is looking at is the one that is right. The outbox looks
/// like the perfect source — <c>WorkItemMoved</c> carries From, To, Because and the moment — and it
/// is not one: <c>OutboxDispatcher.PruneAsync</c> deletes dispatched messages after
/// <c>OutboxOptions.Retention</c>, thirty days by default, so a stream built on it would be
/// complete for a month and then quietly lose the beginning of every story.
///
/// <b>What is not here, and why.</b> Three of the seven statuses have no column of their own —
/// InReview, Blocked and Deployed — so the moves between them exist only in the change trail, which
/// needs <c>audit.view</c>. The audience for this page mostly does not have it: Developer, TechLead,
/// EngineeringManager, QaEngineer, DevOpsEngineer, Designer and Support all hold
/// <c>tasks.view_own</c> and <c>repos.view</c> and none of them holds <c>audit.view</c>. So a
/// developer gets what the repositories reported and what people wrote, and the page says so in a
/// sentence rather than letting them conclude the work was never moved. Labels have no timestamp at
/// all and are permanently outside any stream. And the brief's own example line "12:20 Client
/// approved requirement" cannot be built: no approval is ever raised against a work item —
/// <c>ApprovalService.RequestAsync</c>'s callers are requisitions, purchases, expenses and payroll
/// — so there is no stored fact behind it and inventing one would be worse than leaving it out.
/// </remarks>
public static class Streamed
{
    /// <summary>
    /// Field names in the change trail that the row's own columns already word better.
    /// </summary>
    /// <remarks>
    /// A move to InProgress writes <c>Status</c> and <c>StartedAt</c> in the same audit entry,
    /// because both columns moved in one save. Wording both would put "Work started." beside "Moved
    /// to in progress." at the same minute, twice over — once from <c>WorkStoryFacts</c> and once
    /// from the trail. The row's timestamps are the better source for those two: they are there for
    /// every reader, including the ones without <c>audit.view</c>, so the line has to come from
    /// them and the trail has to stay quiet about them.
    /// </remarks>
    private static readonly HashSet<string> AlreadySaid = new(StringComparer.Ordinal)
    {
        nameof(WorkItem.StartedAt),
        nameof(WorkItem.CompletedAt),
    };

    /// <summary>
    /// Every line, newest first.
    /// </summary>
    /// <param name="trail">
    /// The change trail, or null where the reader may not read it. Null rather than an empty page,
    /// so the page can tell "nothing was moved" from "you cannot see what was moved" — those are
    /// different sentences and only one of them is an apology.
    /// </param>
    /// <param name="names">
    /// Everybody's name by identifier, from <c>PeopleQueries.NamesAsync</c>. Passed in whole rather
    /// than looked up per line: a work item with forty commits would otherwise be forty queries.
    /// It has to include people who have left, which is why the roster already on the page is the
    /// wrong dictionary — it is filtered to active staff, and a leaver's comment would render blank.
    /// </param>
    /// <param name="handles">
    /// Claimed provider logins against staff names, from <c>EngineeringQueries.ClaimedHandlesAsync</c>.
    /// </param>
    public static IReadOnlyList<StreamLine> For(
        WorkStoryFacts facts,
        WorkEvidence evidence,
        AuditPage? trail,
        IReadOnlyDictionary<Guid, string> names,
        IReadOnlyDictionary<string, string> handles)
    {
        var lines = new List<StreamLine>();

        Itself(lines, facts, names);
        Repositories(lines, evidence, handles);

        if (trail is not null)
        {
            Moves(lines, trail, names);
        }

        /*
         * Ordered on the DateTimeOffset itself, which compares instants and not wall clocks. That
         * matters here more than anywhere else on the page: a commit made in Nairobi arrives from
         * GitHub carrying +03:00 and a build's timestamp is usually UTC, so sorting on the clock
         * face would put a commit three hours after the build it triggered.
         */
        return [.. lines.OrderByDescending(line => line.At)];
    }

    /// <summary>
    /// The lines in day order, for headings.
    /// </summary>
    /// <remarks>
    /// Keyed on the local date, because a heading is read as a day on a calendar and a reader in
    /// Nairobi looking at an 01:30 commit wants it under today and not under yesterday in UTC. The
    /// lines under each heading keep the newest-first order they arrived in.
    /// </remarks>
    public static IEnumerable<IGrouping<DateOnly, StreamLine>> ByDay(
        IReadOnlyList<StreamLine> lines) =>
        lines.GroupBy(line => DateOnly.FromDateTime(line.At.ToLocalTime().DateTime));

    /// <summary>What the work item's own row and its owned collections know.</summary>
    private static void Itself(
        List<StreamLine> lines, WorkStoryFacts facts, IReadOnlyDictionary<Guid, string> names)
    {
        if (facts.StartedAt is { } started)
        {
            lines.Add(new StreamLine(started, "Work started."));
        }

        /*
         * One column, two sentences. MoveTo stamps CompletedAt for Done AND for Cancelled, and the
         * move from Done to Deployed deliberately leaves it at the acceptance moment — so a line
         * worded off the column alone would tell every cancelled card it had been accepted, which
         * is the most misleading single sentence this file could produce.
         */
        if (facts.CompletedAt is { } finished)
        {
            lines.Add(facts.Status == WorkItemStatus.Cancelled
                ? new StreamLine(finished, "Cancelled.", "pill--quiet", "cancelled")
                : new StreamLine(finished, "Accepted as done.", "pill--done", "accepted"));
        }

        foreach (var said in facts.Comments)
        {
            // Not the comment's body. The thread further down the same page prints every one of
            // them in full, and a stream repeating them stops being a stream of what happened.
            var edited = said.EditedAt is null ? string.Empty : " Edited later.";

            lines.Add(new StreamLine(
                said.At,
                $"{Somebody(names, said.ByEmployeeId)} wrote on the thread.{edited}",
                Link: "#thread",
                LinkSaid: "Read it"));
        }

        foreach (var ticked in facts.Ticked)
        {
            lines.Add(new StreamLine(
                ticked.MetAt,
                $"{Somebody(names, ticked.MetById)} ticked: {ticked.Text}",
                "pill--done",
                "ticked"));
        }
    }

    /// <summary>What the repositories reported.</summary>
    private static void Repositories(
        List<StreamLine> lines, WorkEvidence evidence, IReadOnlyDictionary<string, string> handles)
    {
        foreach (var commit in evidence.Commits)
        {
            lines.Add(new StreamLine(
                commit.At,
                $"{Person(handles, commit.Author)} pushed {commit.Short} — {FirstLine(commit.Message)}"));
        }

        foreach (var pullRequest in evidence.PullRequests)
        {
            lines.Add(new StreamLine(
                pullRequest.OpenedAt,
                $"{Person(handles, pullRequest.Author)} opened pull request #{pullRequest.Number}."));

            foreach (var verdict in pullRequest.Verdicts)
            {
                var (said, tone, chip) = verdict.Verdict switch
                {
                    ReviewVerdict.Approved => ("approved", "pill--done", "approved"),
                    ReviewVerdict.ChangesRequested =>
                        ("asked for changes on", "pill--danger", "changes asked for"),
                    _ => ("commented on", null, null),
                };

                lines.Add(new StreamLine(
                    verdict.At,
                    $"{Person(handles, verdict.Reviewer)} {said} #{pullRequest.Number}.",
                    tone,
                    chip));
            }

            /*
             * Only where the provider told us when. ClosedAt is null for a pull request that is
             * still open, and a merge dated on anything else would be a guess about the one moment
             * on this stream that people quote at each other.
             */
            if (pullRequest.ClosedAt is { } closed)
            {
                lines.Add(pullRequest.State == PullRequestState.Merged
                    ? new StreamLine(
                        closed, $"#{pullRequest.Number} was merged.", "pill--done", "merged")
                    : new StreamLine(
                        closed,
                        $"#{pullRequest.Number} was closed without merging.",
                        "pill--quiet",
                        "closed"));
            }
        }

        foreach (var build in evidence.Builds)
        {
            /*
             * Dated on when it finished, falling back to when it started. A build still running has
             * no finish, and the fall-back is what puts it on the stream at all rather than leaving
             * the one line somebody is waiting for off the list.
             */
            var said = build.Outcome switch
            {
                BuildOutcome.Passed when build.Took is { } took =>
                    $"{build.Name} passed on {build.Short} in {took}.",
                BuildOutcome.Passed => $"{build.Name} passed on {build.Short}.",
                BuildOutcome.Failed => $"{build.Name} failed on {build.Short}.",
                BuildOutcome.Running => $"{build.Name} is still going on {build.Short}.",
                _ => $"{build.Name} on {build.Short}: {Words.For(build.Outcome).ToLowerInvariant()}.",
            };

            /*
              * The chip's word comes from the same Words overload the tables below use, so the
              * stream and the table beside it cannot disagree about what a build outcome is called.
              */
            lines.Add(new StreamLine(
                build.FinishedAt ?? build.StartedAt,
                said,
                Words.PillFor(build.Outcome),
                Words.For(build.Outcome).ToLowerInvariant(),
                build.Url,
                "The log"));
        }

        foreach (var deployment in evidence.Deployments)
        {
            var where = Words.For(deployment.Environment).ToLowerInvariant();

            if (deployment.NameAddsSomething)
            {
                where += $" ({deployment.EnvironmentName})";
            }

            var said = deployment.State switch
            {
                DeploymentState.Succeeded => $"Reached {where}.",
                DeploymentState.Failed => $"A deploy to {where} failed.",
                _ => $"A deploy to {where} is running.",
            };

            // FinishedAt rather than At, which Deployment documents as when the deploy BEGAN. A
            // deploy that took eleven minutes would otherwise put "reached production" eleven
            // minutes before production had it.
            lines.Add(new StreamLine(
                deployment.FinishedAt ?? deployment.At,
                said,
                Words.PillFor(deployment.State),
                Words.For(deployment.State).ToLowerInvariant(),
                deployment.Url,
                "The deploy"));
        }
    }

    /// <summary>The moves and edits, which live only in the change trail.</summary>
    private static void Moves(
        List<StreamLine> lines, AuditPage trail, IReadOnlyDictionary<Guid, string> names)
    {
        foreach (var entry in trail.Entries)
        {
            var actor = entry.ActorName ?? "The system";

            if (entry.Action.EndsWith(".added", StringComparison.Ordinal))
            {
                lines.Add(new StreamLine(entry.OccurredAt, $"{actor} raised it."));
                continue;
            }

            var changes = entry.Changes()
                .Where(change => !AlreadySaid.Contains(change.Key))
                .ToList();

            if (changes.Count == 0)
            {
                continue;
            }

            var said = changes.Count == 1
                ? Sentence(changes[0].Key, changes[0].Value.From, changes[0].Value.To, actor, names)
                : null;

            /*
             * One honest sentence naming the fields, for anything with no wording of its own or
             * several changes in one save. Never a before-and-after pair: that is exactly the
             * "list of column changes rather than sentences" the section objects to, and it is the
             * shape this whole file exists to replace. Naming the fields at least tells somebody
             * what to go and look at.
             */
            said ??= $"{actor} changed "
                + $"{Words.Count(changes.Count, "field")}: "
                + string.Join(", ", changes.Select(change => Field(change.Key)).Order()) + ".";

            lines.Add(new StreamLine(entry.OccurredAt, said, Link: "#trail"));
        }
    }

    /// <summary>
    /// One changed field, as a sentence.
    /// </summary>
    /// <remarks>
    /// Null where there is no honest sentence for it, so the caller falls back to naming the field.
    /// A switch arm that guessed would be worse than the fallback: the fallback is vague and true,
    /// and a guess reads as certain.
    /// </remarks>
    private static string? Sentence(
        string field,
        string? from,
        string? to,
        string actor,
        IReadOnlyDictionary<Guid, string> names) => field switch
        {
            nameof(WorkItem.Status) when Enum.TryParse<WorkItemStatus>(to, out var status) =>
                $"Moved to {Words.For(status).ToLowerInvariant()}.",

            // Taken off somebody rather than given to nobody, because unassigning is a decision
            // and "assigned to nothing" reads as a field having been cleared by accident.
            nameof(WorkItem.AssigneeId) when to is null && Guid.TryParse(from, out var was) =>
                $"Taken off {Somebody(names, was)}.",
            nameof(WorkItem.AssigneeId) when Guid.TryParse(to, out var now) =>
                $"Given to {Somebody(names, now)}.",

            nameof(WorkItem.BlockedReason) when to is null => "Unblocked.",
            nameof(WorkItem.BlockedReason) => $"Blocked: {to}",

            nameof(WorkItem.DueOn) when to is null => "The date it was due was taken off.",
            nameof(WorkItem.DueOn) when DateOnly.TryParse(to, out var due) =>
                $"Due {due:d MMM yyyy}.",

            nameof(WorkItem.Priority) when Enum.TryParse<Priority>(to, out var priority) =>
                $"Priority set to {priority.ToString().ToLowerInvariant()}.",

            nameof(WorkItem.EstimateMinutes) when to is null => "The estimate was taken off.",
            nameof(WorkItem.EstimateMinutes) when int.TryParse(to, out var minutes) =>
                $"Estimated at {Words.HowLong(TimeSpan.FromMinutes(minutes))}.",

            nameof(WorkItem.ProjectId) when to is null => "Taken off its project.",
            nameof(WorkItem.ProjectId) => "Moved to another project.",

            nameof(WorkItem.SprintId) when to is null => "Taken out of its sprint.",
            nameof(WorkItem.SprintId) => "Put into a sprint.",

            nameof(WorkItem.ParentId) when to is null => "No longer sits under anything.",
            nameof(WorkItem.ParentId) => "Moved under a different parent.",

            nameof(WorkItem.Kind) when Enum.TryParse<WorkItemKind>(to, out var kind) =>
                $"Now a {WorkItem.Name(kind)}.",

            // The new title is worth saying; a paragraph of detail is not, and the page below
            // shows whatever it currently says.
            nameof(WorkItem.Title) => $"Renamed to “{to}”.",
            nameof(WorkItem.Detail) when to is null => "The description was cleared.",
            nameof(WorkItem.Detail) => "The description was rewritten.",

            _ => null,
        };

    /// <summary>
    /// A field name as somebody would say it.
    /// </summary>
    /// <remarks>
    /// Only reached by the fallback, and only for names with no sentence of their own, so this is
    /// deliberately thin: splitting the camel case is honest about being a column name, where a
    /// hand-written phrase for each would be a second place to keep the same list.
    /// </remarks>
    private static string Field(string name) =>
        string.Concat(name.Select((character, index) =>
            index > 0 && char.IsUpper(character) ? $" {char.ToLowerInvariant(character)}" : $"{character}"));

    /// <summary>
    /// The person behind a provider login.
    /// </summary>
    /// <remarks>
    /// The claimed staff name where a <c>Contributor</c> claim exists, and the login verbatim
    /// otherwise. Never a guess at who a login belongs to: an unclaimed handle on a stream is a
    /// prompt to claim it, and a name matched by spelling would be an accusation.
    /// </remarks>
    private static string Person(IReadOnlyDictionary<string, string> handles, string handle) =>
        handles.TryGetValue(handle, out var name) ? name : handle;

    /// <summary>
    /// The staff name behind an identifier.
    /// </summary>
    /// <remarks>
    /// The same sentence the work item's own thread uses for a comment by somebody who has left,
    /// because the dictionary includes leavers and a miss here means the record is genuinely gone.
    /// </remarks>
    private static string Somebody(IReadOnlyDictionary<Guid, string> names, Guid? id) =>
        id is { } who && names.TryGetValue(who, out var name)
            ? name
            : "Somebody no longer on the staff list";

    /// <summary>
    /// The first line of a commit message, which is the summary by convention.
    /// </summary>
    /// <remarks>
    /// A stream line is one sentence, and a commit message with a body would otherwise put five
    /// paragraphs between two timestamps. Trimmed rather than truncated at a character count: the
    /// convention already gives a summary line, so cutting at the newline takes what the author
    /// meant as the summary rather than the first eighty characters of it.
    /// </remarks>
    private static string FirstLine(string message)
    {
        var first = message.AsSpan();
        var breakAt = first.IndexOfAny('\n', '\r');

        if (breakAt >= 0)
        {
            first = first[..breakAt];
        }

        var said = first.Trim().ToString();

        return said.EndsWith('.') ? said : said + ".";
    }
}
