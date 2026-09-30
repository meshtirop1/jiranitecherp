using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.Audit;
using JiranisokoTech.Infrastructure.Engineering;
using JiranisokoTech.Infrastructure.Work;
using JiranisokoTech.Web;

namespace JiranisokoTech.Tests.Work;

/// <summary>
/// The sentences a work item's activity stream is made of.
/// </summary>
/// <remarks>
/// <b>Section 71 asks for a timeline and shows the shape it means:</b> "13:32 Task moved to QA",
/// "13:58 Sarah approved #182". What the checklist row objected to was a list of column changes
/// instead — "Status: InProgress → InReview" is a row out of a database and tells a reader to do the
/// translating. So the thing worth testing is the wording, and it is testable without a database
/// because <c>Streamed</c> is a pure function over rows: the same shape <c>HowLongTests</c> uses.
///
/// Every line here is a fact that was already stored before that file existed. The tests are about
/// which sentence comes out of each one, and about the three cases where a plausible reading of a
/// column would produce a confident falsehood.
/// </remarks>
public class StreamedTests
{
    private static readonly Guid Item = Guid.CreateVersion7();

    private static readonly Guid Mesh = Guid.CreateVersion7();

    private static readonly Guid Gone = Guid.CreateVersion7();

    private static readonly Dictionary<Guid, string> Names =
        new() { [Mesh] = "Mesh Tirop" };

    private static readonly Dictionary<string, string> Handles =
        new(StringComparer.OrdinalIgnoreCase) { ["meshtirop1"] = "Mesh Tirop" };

    /// <summary>
    /// Newest first, by instant and not by the clock on the wall.
    /// </summary>
    /// <remarks>
    /// The one ordering fault this file could have, and it would be invisible in a test whose
    /// timestamps all shared an offset. A commit made in Nairobi arrives from GitHub carrying
    /// +03:00 and a build's timestamp is usually UTC, so a stream sorted on the clock face would put
    /// a commit three hours after the build it triggered. 14:10+03:00 is 11:10Z, so the 12:00Z
    /// comment is the later of these two and has to come first.
    /// </remarks>
    [Fact]
    public void Lines_come_back_newest_first_across_offsets()
    {
        var nairobi = new DateTimeOffset(2026, 9, 30, 14, 10, 0, TimeSpan.FromHours(3));
        var utc = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

        var lines = Streamed.For(
            Facts(comments: [new SaidRow(Mesh, utc, null)]),
            new WorkEvidence([Commit(nairobi)], []),
            null,
            Names,
            Handles);

        Assert.Equal(2, lines.Count);
        Assert.Contains("wrote on the thread", lines[0].Said);
        Assert.Contains("pushed", lines[1].Said);
    }

    /// <summary>
    /// A status change is a sentence, not a pair of column values.
    /// </summary>
    /// <remarks>
    /// The assertion the section's own complaint reduces to. "InReview" is what the trail stores and
    /// "Status" is the column it stores it under, and neither belongs on a screen.
    /// </remarks>
    [Fact]
    public void A_move_reads_as_a_sentence_and_never_as_a_column()
    {
        var lines = Streamed.For(
            Facts(), Empty, Trail(Moved(WorkItemStatus.InProgress, WorkItemStatus.InReview)),
            Names, Handles);

        var line = Assert.Single(lines);

        Assert.Equal("Moved to in review.", line.Said);
        Assert.DoesNotContain("InReview", line.Said);
        Assert.DoesNotContain("Status", line.Said);
    }

    /// <summary>
    /// A reader who cannot see the trail gets the rest of the stream, not an empty one.
    /// </summary>
    /// <remarks>
    /// Which is the state most readers of this page are in: Developer, TechLead, QaEngineer,
    /// DevOpsEngineer, Designer and Support all hold <c>tasks.view_own</c> and <c>repos.view</c> and
    /// none holds <c>audit.view</c>. Null trail rather than an empty page precisely so the page can
    /// say the moves are missing instead of implying there were none.
    /// </remarks>
    [Fact]
    public void Without_the_trail_the_moves_are_gone_and_nothing_else_is()
    {
        var at = new DateTimeOffset(2026, 9, 30, 9, 0, 0, TimeSpan.Zero);

        var lines = Streamed.For(
            Facts(started: at, comments: [new SaidRow(Mesh, at.AddHours(1), null)]),
            new WorkEvidence([Commit(at.AddHours(2))], []),
            null,
            Names,
            Handles);

        Assert.Equal(3, lines.Count);
        Assert.DoesNotContain(lines, line => line.Said.StartsWith("Moved to", StringComparison.Ordinal));
    }

    /// <summary>
    /// The row's own timestamps word the start, and the trail stays quiet about it.
    /// </summary>
    /// <remarks>
    /// <b>The duplicate this file's design makes easy.</b> A move to InProgress writes Status AND
    /// StartedAt in one save, because both columns moved together, so a trail rendering every
    /// changed field produces "Work started." beside "Moved to in progress." at the same minute —
    /// and <c>WorkStoryFacts</c> has already said the first of those from the column itself. The
    /// column is the better source because it is there for every reader, including the ones who
    /// cannot see the trail, so the trail is the half that has to say nothing.
    /// </remarks>
    [Fact]
    public void A_start_is_said_once_although_two_sources_hold_it()
    {
        var at = new DateTimeOffset(2026, 9, 30, 8, 30, 0, TimeSpan.Zero);

        var entry = AuditEntry.Record(
            "work_item.modified",
            nameof(WorkItem),
            Item,
            at,
            actorName: "Mesh Tirop",
            before: new Dictionary<string, string?>
            {
                [nameof(WorkItem.Status)] = nameof(WorkItemStatus.Todo),
                [nameof(WorkItem.StartedAt)] = null,
            },
            after: new Dictionary<string, string?>
            {
                [nameof(WorkItem.Status)] = nameof(WorkItemStatus.InProgress),
                [nameof(WorkItem.StartedAt)] = at.ToString("O"),
            });

        var lines = Streamed.For(
            Facts(started: at), Empty, Trail(entry), Names, Handles);

        Assert.Equal(2, lines.Count);
        Assert.Single(lines, line => line.Said == "Work started.");
        Assert.Single(lines, line => line.Said == "Moved to in progress.");
    }

    /// <summary>
    /// A cancelled work item is not told it was accepted.
    /// </summary>
    /// <remarks>
    /// <c>MoveTo</c> stamps <c>CompletedAt</c> for Done AND for Cancelled, and leaves it at the
    /// acceptance moment when Done becomes Deployed. One column, three meanings — so a sentence
    /// worded off the column alone would congratulate every abandoned card. The most misleading
    /// single sentence this file could produce, and the cheapest to get wrong.
    /// </remarks>
    [Theory]
    [InlineData(WorkItemStatus.Done, "Accepted as done.")]
    [InlineData(WorkItemStatus.Deployed, "Accepted as done.")]
    [InlineData(WorkItemStatus.Cancelled, "Cancelled.")]
    public void One_column_of_completion_words_two_different_endings(
        WorkItemStatus status, string expected)
    {
        var at = new DateTimeOffset(2026, 9, 30, 17, 0, 0, TimeSpan.Zero);

        var lines = Streamed.For(
            Facts(completed: at, status: status), Empty, null, Names, Handles);

        Assert.Equal(expected, Assert.Single(lines).Said);
    }

    /// <summary>
    /// An unrecognised change names its fields rather than printing a before and after.
    /// </summary>
    /// <remarks>
    /// Two things are being asserted at once and both matter. The line is not dropped, because a
    /// change nobody wrote a sentence for still happened; and it does not render the pair, because
    /// that is the column-listing the section objects to and it would arrive by the easiest possible
    /// route — a fallback that just formats whatever it was given.
    /// </remarks>
    [Fact]
    public void A_change_with_no_sentence_names_the_field_and_not_the_values()
    {
        var at = new DateTimeOffset(2026, 9, 30, 11, 0, 0, TimeSpan.Zero);

        var entry = AuditEntry.Record(
            "work_item.modified",
            nameof(WorkItem),
            Item,
            at,
            actorName: "Mesh Tirop",
            before: new Dictionary<string, string?> { ["SomethingNobodyWordedYet"] = "before" },
            after: new Dictionary<string, string?> { ["SomethingNobodyWordedYet"] = "after" });

        var line = Assert.Single(Streamed.For(
            Facts(), Empty, Trail(entry), Names, Handles));

        Assert.Equal("Mesh Tirop changed 1 field: Something nobody worded yet.", line.Said);
        Assert.DoesNotContain("before", line.Said);
        Assert.DoesNotContain("after", line.Said);
    }

    /// <summary>
    /// A build still running is dated on when it started, and says so.
    /// </summary>
    /// <remarks>
    /// The one line on this stream somebody is actively waiting for. Dating it on
    /// <c>FinishedAt</c> alone would leave it off the list entirely, which is the opposite of
    /// useful.
    /// </remarks>
    [Fact]
    public void A_build_that_has_not_finished_is_dated_on_its_start()
    {
        var started = new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);

        var build = new BuildRow(
            "ci", "abc1234def", "main", BuildOutcome.Running, started, null, "https://ci.example/1");

        var line = Assert.Single(Streamed.For(
            Facts(), new WorkEvidence([], []) { Builds = [build] }, null, Names, Handles));

        Assert.Equal(started, line.At);
        Assert.Contains("still going", line.Said);
    }

    /// <summary>
    /// A deployment is dated on when it landed, not on when it was started.
    /// </summary>
    /// <remarks>
    /// <c>Deployment.At</c> is documented as the moment the deploy BEGAN, and every screen before
    /// this one only had to say which deploy was latest, for which that is the right column. A line
    /// reading "reached production" is about the moment production had it, so a deploy that took
    /// eleven minutes would otherwise be eleven minutes wrong about the only fact the line states.
    /// </remarks>
    [Fact]
    public void A_deployment_is_dated_on_when_it_landed()
    {
        var began = new DateTimeOffset(2026, 9, 30, 15, 0, 0, TimeSpan.Zero);
        var landed = began.AddMinutes(11);

        var deployment = new DeploymentRow(
            DeploymentEnvironment.Production,
            "production",
            "abc1234def",
            "main",
            "meshtirop1",
            DeploymentState.Succeeded,
            began,
            null)
        {
            FinishedAt = landed,
        };

        var line = Assert.Single(Streamed.For(
            Facts(),
            new WorkEvidence([], []) { Deployments = [deployment] },
            null,
            Names,
            Handles));

        Assert.Equal(landed, line.At);
        Assert.Equal("Reached production.", line.Said);
    }

    /// <summary>
    /// A merge is a sentence of its own, and a close without merging is a different one.
    /// </summary>
    /// <remarks>
    /// Section 12's own rule, repeated here because the stream is a second place it can be broken:
    /// closed is not merged, and a stream that said "merged" for both would be reporting work as
    /// delivered that was abandoned.
    /// </remarks>
    [Theory]
    [InlineData(PullRequestState.Merged, "#7 was merged.")]
    [InlineData(PullRequestState.Closed, "#7 was closed without merging.")]
    public void A_closed_pull_request_says_whether_it_was_merged(
        PullRequestState state, string expected)
    {
        var opened = new DateTimeOffset(2026, 9, 30, 9, 0, 0, TimeSpan.Zero);

        var pullRequest = new PullRequestRow(
            7, "The change", "meshtirop1", "feature/7", "jiranisokotech/erp", state, opened, 1, true)
        {
            ClosedAt = opened.AddHours(4),
        };

        var lines = Streamed.For(
            Facts(), new WorkEvidence([], [pullRequest]), null, Names, Handles);

        Assert.Contains(lines, line => line.Said == expected);
    }

    /// <summary>
    /// Each reviewer's verdict is its own line, naming who and when.
    /// </summary>
    /// <remarks>
    /// The facts for section 71's own example — "13:58 Sarah approved #182" — were stored in
    /// <c>pull_request_reviews</c> and unreachable from any screen, because <c>PullRequestRow</c>
    /// kept a COUNT of reviews and one <c>IsApproved</c> flag. A count answers "has anybody looked",
    /// which is the table's question, and cannot answer "what happened, in order".
    /// </remarks>
    [Fact]
    public void Every_review_is_its_own_line()
    {
        var opened = new DateTimeOffset(2026, 9, 30, 9, 0, 0, TimeSpan.Zero);

        var pullRequest = new PullRequestRow(
            182, "The change", "meshtirop1", "feature/182", "jiranisokotech/erp",
            PullRequestState.Open, opened, 2, false)
        {
            Verdicts =
            [
                new ReviewRow("sarah", ReviewVerdict.ChangesRequested, opened.AddHours(1)),
                new ReviewRow("meshtirop1", ReviewVerdict.Approved, opened.AddHours(4).AddMinutes(58)),
            ],
        };

        var lines = Streamed.For(
            Facts(), new WorkEvidence([], [pullRequest]), null, Names, Handles);

        Assert.Contains(lines, line => line.Said == "sarah asked for changes on #182.");
        Assert.Contains(lines, line => line.Said == "Mesh Tirop approved #182.");
    }

    /// <summary>
    /// An unclaimed provider login is printed as itself.
    /// </summary>
    /// <remarks>
    /// Never a guess at whose it is. A handle nobody has claimed is a prompt to claim it, and a name
    /// matched by spelling would put one person's name against another person's commit — which on a
    /// record of who did what is an accusation.
    /// </remarks>
    [Fact]
    public void An_unclaimed_handle_keeps_its_login()
    {
        var at = new DateTimeOffset(2026, 9, 30, 13, 0, 0, TimeSpan.Zero);

        var line = Assert.Single(Streamed.For(
            Facts(),
            new WorkEvidence([Commit(at, author: "some-contractor")], []),
            null,
            Names,
            Handles));

        Assert.StartsWith("some-contractor pushed", line.Said);
    }

    /// <summary>
    /// Somebody who has left still has their line.
    /// </summary>
    /// <remarks>
    /// They wrote the comment. A stream that rendered a blank where their name goes has lost the
    /// fact rather than protected anything, and it is why the dictionary passed in comes from
    /// <c>NamesAsync</c> and not from the active-staff roster the page already had loaded.
    /// </remarks>
    [Fact]
    public void A_comment_by_somebody_no_longer_here_still_has_a_line()
    {
        var at = new DateTimeOffset(2026, 9, 30, 10, 30, 0, TimeSpan.Zero);

        var line = Assert.Single(Streamed.For(
            Facts(comments: [new SaidRow(Gone, at, null)]), Empty, null, Names, Handles));

        Assert.Equal("Somebody no longer on the staff list wrote on the thread.", line.Said);
    }

    /// <summary>
    /// A commit's line is the summary, not the whole message.
    /// </summary>
    /// <remarks>
    /// The convention gives a summary line and a body, so cutting at the newline takes what the
    /// author meant as the summary rather than the first eighty characters of it. A stream is one
    /// sentence per line, and five paragraphs between two timestamps stops being one.
    /// </remarks>
    [Fact]
    public void A_commit_line_stops_at_the_summary()
    {
        var at = new DateTimeOffset(2026, 9, 30, 16, 0, 0, TimeSpan.Zero);

        var line = Assert.Single(Streamed.For(
            Facts(),
            new WorkEvidence(
                [Commit(at, message: "Fix the total\n\nThe rounding was done twice.")], []),
            null,
            Names,
            Handles));

        Assert.Equal("Mesh Tirop pushed abc1234 — Fix the total.", line.Said);
        Assert.DoesNotContain("rounding", line.Said);
    }

    /// <summary>
    /// Unassigning says who it was taken off, rather than that it was given to nobody.
    /// </summary>
    /// <remarks>
    /// Because taking work off somebody is a decision, and "assigned to nothing" reads as a field
    /// cleared by accident. The trail stores the same change either way — one key, a value on one
    /// side — so the direction has to be read rather than assumed.
    /// </remarks>
    [Fact]
    public void Taking_work_off_somebody_says_whose_it_was()
    {
        var at = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

        var entry = AuditEntry.Record(
            "work_item.modified",
            nameof(WorkItem),
            Item,
            at,
            actorName: "Mesh Tirop",
            before: new Dictionary<string, string?> { [nameof(WorkItem.AssigneeId)] = Mesh.ToString() },
            after: new Dictionary<string, string?> { [nameof(WorkItem.AssigneeId)] = null });

        var line = Assert.Single(Streamed.For(
            Facts(), Empty, Trail(entry), Names, Handles));

        Assert.Equal("Taken off Mesh Tirop.", line.Said);
    }

    /// <summary>
    /// Days are grouped on the local date.
    /// </summary>
    /// <remarks>
    /// A heading is read as a day on a calendar, so a reader in Nairobi looking at a 01:30 line
    /// wants it under today and not under yesterday in UTC. Asserted on the count rather than the
    /// key, because the key depends on where the suite is running and the thing being tested is that
    /// two instants an hour apart are not split across two headings.
    /// </remarks>
    [Fact]
    public void Two_lines_an_hour_apart_share_one_heading()
    {
        var noon = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

        var lines = Streamed.For(
            Facts(comments: [new SaidRow(Mesh, noon, null)]),
            new WorkEvidence([Commit(noon.AddHours(1))], []),
            null,
            Names,
            Handles);

        Assert.Single(Streamed.ByDay(lines));
    }

    /// <summary>
    /// The three lines that share the quiet colour are labelled three different things.
    /// </summary>
    /// <remarks>
    /// <b>The fault that made the chip's word a field rather than a lookup.</b> A cancelled work
    /// item, a pull request closed without merging and a cancelled build all wear
    /// <c>pill--quiet</c>, and the first version derived the word from the class — so one word had
    /// to describe all three. It said "closed" on every one of them, which is a small untruth about
    /// two of the three in the one place a reader scanning a stream looks first.
    /// </remarks>
    [Fact]
    public void One_colour_does_not_mean_one_word()
    {
        var at = new DateTimeOffset(2026, 9, 30, 14, 0, 0, TimeSpan.Zero);

        var pullRequest = new PullRequestRow(
            7, "The change", "meshtirop1", "feature/7", "jiranisokotech/erp",
            PullRequestState.Closed, at, 0, false)
        {
            ClosedAt = at,
        };

        var build = new BuildRow(
            "ci", "abc1234def", "main", BuildOutcome.Cancelled, at, at, null);

        var lines = Streamed.For(
            Facts(completed: at, status: WorkItemStatus.Cancelled),
            new WorkEvidence([], [pullRequest]) { Builds = [build] },
            null,
            Names,
            Handles);

        var quiet = lines
            .Where(line => line.Tone == "pill--quiet")
            .Select(line => line.ToneSaid)
            .ToList();

        Assert.Equal(3, quiet.Count);

        /*
         * Not three different words -- two of these three ARE both cancelled, the work item and
         * the build, and "cancelled" is the right chip on each. The first version of this test
         * asserted all three were distinct and failed, and the test was what was wrong: the
         * property being defended is that the word follows the FACT, not that one colour implies
         * three words. What the colour-derived version got wrong is the one below.
         */
        Assert.Equal(2, quiet.Count(word => word == "cancelled"));
        Assert.Single(quiet, word => word == "closed");
    }

    /// <summary>
    /// A build's chip says what the tables below it say.
    /// </summary>
    /// <remarks>
    /// Both come from the same <c>Words</c> overload, so this asserts they cannot drift rather than
    /// asserting a particular word — the word is that method's business and a test repeating it
    /// would just be a second place to change.
    /// </remarks>
    [Fact]
    public void A_builds_chip_is_worded_where_every_other_screen_words_it()
    {
        var at = new DateTimeOffset(2026, 9, 30, 14, 0, 0, TimeSpan.Zero);

        var build = new BuildRow(
            "ci", "abc1234def", "main", BuildOutcome.Failed, at, at, null);

        var line = Assert.Single(Streamed.For(
            Facts(), new WorkEvidence([], []) { Builds = [build] }, null, Names, Handles));

        Assert.Equal(Words.For(BuildOutcome.Failed).ToLowerInvariant(), line.ToneSaid);
        Assert.Equal(Words.PillFor(BuildOutcome.Failed), line.Tone);
    }

    private static WorkEvidence Empty => new([], []);

    private static WorkStoryFacts Facts(
        DateTimeOffset? started = null,
        DateTimeOffset? completed = null,
        WorkItemStatus status = WorkItemStatus.InProgress,
        IReadOnlyList<SaidRow>? comments = null,
        IReadOnlyList<TickedRow>? ticked = null) =>
        new(started, completed, status, comments ?? [], ticked ?? []);

    private static CommitRow Commit(
        DateTimeOffset at, string author = "meshtirop1", string message = "Do the thing") =>
        new("abc1234def5678", message, author, "main", at);

    private static AuditPage Trail(params AuditEntry[] entries) => new(entries, entries.Length, 0);

    private static AuditEntry Moved(WorkItemStatus from, WorkItemStatus to) =>
        AuditEntry.Record(
            "work_item.modified",
            nameof(WorkItem),
            Item,
            new DateTimeOffset(2026, 9, 30, 13, 32, 0, TimeSpan.Zero),
            actorName: "Mesh Tirop",
            before: new Dictionary<string, string?> { [nameof(WorkItem.Status)] = from.ToString() },
            after: new Dictionary<string, string?> { [nameof(WorkItem.Status)] = to.ToString() });
}
