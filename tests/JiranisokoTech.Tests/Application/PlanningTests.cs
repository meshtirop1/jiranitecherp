using JiranisokoTech.Application.People;
using JiranisokoTech.Application.Work;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.People;
using JiranisokoTech.Infrastructure.Work;
using JiranisokoTech.Tests.Infrastructure;

namespace JiranisokoTech.Tests.Application;

/// <summary>
/// Sprints, the backlog, the hierarchy and what waits on what.
/// </summary>
/// <remarks>
/// Section 11's two remaining rows. Most of what is tested here is a refusal, and each one exists
/// because the thing it refuses looks perfectly reasonable at the moment somebody does it:
/// a second running sprint, a card put under something that already sits below it, and a piece of
/// work marked done while the thing it waits on is not.
/// </remarks>
public class PlanningTests
{
    /// <summary>
    /// The backlog is the work in no sprint, and nothing else.
    /// </summary>
    /// <remarks>
    /// Derived rather than kept as its own list, because a second list would need every item to
    /// be in exactly one of the two with nothing enforcing it — and work would end up in both or
    /// in neither. Finished work is left out because the backlog answers "what have we not done
    /// yet".
    /// </remarks>
    [Fact]
    public async Task The_backlog_is_what_is_in_no_sprint_and_not_finished()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var (work, planning, rows) = Services(fixture, context);

        var who = await Somebody(fixture, context, "Brian Kiptoo");

        var first = await work.RaiseAsync("Fit the tracker", who);
        var second = await work.RaiseAsync("Wire the depot", who);
        var third = await work.RaiseAsync("Write the rota", who);

        Assert.Equal(3, (await planning.BacklogAsync()).Count);

        var sprint = await planning.PlanAsync(
            "Sprint 14", fixture.Clock.Today, fixture.Clock.Today.AddDays(13));

        await planning.ScheduleAsync(first.Id, sprint.Id);

        Assert.Equal(2, (await planning.BacklogAsync()).Count);
        Assert.Single(await planning.InSprintAsync(sprint.Id));

        // Finished work leaves the backlog without going anywhere.
        await work.MoveAsync(second.Id, WorkItemStatus.Cancelled);

        var backlog = await planning.BacklogAsync();

        Assert.Single(backlog);
        Assert.Equal(third.Id, backlog[0].Id);
    }

    /// <summary>
    /// Two sprints cannot run at once.
    /// </summary>
    /// <remarks>
    /// "The current sprint" is a phrase this system has to be able to answer, and with two
    /// running it has no answer — every board and every "what are we doing this week" would have
    /// to ask which. The refusal names the one already running, because the useful next action is
    /// to go and finish it.
    /// </remarks>
    [Fact]
    public async Task Only_one_sprint_runs_at_a_time()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var (_, planning, _) = Services(fixture, context);

        var first = await planning.PlanAsync(
            "Sprint 14", fixture.Clock.Today, fixture.Clock.Today.AddDays(13));

        var second = await planning.PlanAsync(
            "Sprint 15", fixture.Clock.Today.AddDays(14), fixture.Clock.Today.AddDays(27));

        await planning.StartSprintAsync(first.Id);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => planning.StartSprintAsync(second.Id));

        Assert.Contains("Sprint 14 is already running", refusal.Message);

        await planning.FinishSprintAsync(first.Id);
        await planning.StartSprintAsync(second.Id);

        Assert.Equal(second.Id, (await planning.RunningAsync())!.Id);
    }

    /// <summary>
    /// Finishing a sprint sends what is unfinished back to the backlog, and says how much.
    /// </summary>
    /// <remarks>
    /// Back to the backlog rather than into the next sprint, because carrying work forward
    /// automatically is how a sprint fills up before anybody decided what was in it. The count is
    /// recorded on the row because it is a fact about a moment: counting open items later gives a
    /// different answer every time somebody finishes one afterwards.
    /// </remarks>
    [Fact]
    public async Task Finishing_a_sprint_returns_what_is_unfinished_and_records_how_much()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var (work, planning, rows) = Services(fixture, context);

        var who = await Somebody(fixture, context, "Brian Kiptoo");

        var done = await work.RaiseAsync("Fit the tracker", who);
        var notDone = await work.RaiseAsync("Wire the depot", who);

        var sprint = await planning.PlanAsync(
            "Sprint 14", fixture.Clock.Today, fixture.Clock.Today.AddDays(13));

        await planning.ScheduleAsync(done.Id, sprint.Id);
        await planning.ScheduleAsync(notDone.Id, sprint.Id);
        await planning.StartSprintAsync(sprint.Id);

        await work.MoveAsync(done.Id, WorkItemStatus.InProgress);
        await work.MoveAsync(done.Id, WorkItemStatus.InReview);
        await work.MoveAsync(done.Id, WorkItemStatus.Done);

        var carried = await planning.FinishSprintAsync(sprint.Id);

        Assert.Equal(1, carried);
        Assert.Equal(1, (await planning.SprintAsync(sprint.Id))!.CarriedOver);

        // The unfinished one is back on the backlog; the finished one stays in the sprint, which
        // is what makes the sprint a record of what it did.
        Assert.Single(await planning.BacklogAsync());
        Assert.Single(await planning.InSprintAsync(sprint.Id));
    }

    /// <summary>
    /// A parent has to be bigger than what sits under it.
    /// </summary>
    /// <remarks>
    /// The rule that makes epics, features, stories and subtasks mean anything. Without it the
    /// kinds are decoration: an epic under a subtask is allowed, and the words stop telling
    /// anybody about the shape of the work.
    /// </remarks>
    [Fact]
    public async Task A_parent_has_to_be_bigger_than_its_child()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var (work, planning, rows) = Services(fixture, context);

        var who = await Somebody(fixture, context, "Brian Kiptoo");

        var epic = await work.RaiseAsync("Invoicing", who);
        var story = await work.RaiseAsync("Pay from the email", who);

        await planning.KindAsync(epic.Id, WorkItemKind.Epic);
        await planning.KindAsync(story.Id, WorkItemKind.Story);

        await planning.ParentAsync(story.Id, epic.Id);

        Assert.Equal(epic.Id, (await rows.FindAsync(story.Id))!.ParentId);

        /*
         * The size rule, on two items that are not already related — because when they are, the
         * loop check fires first and is right to: putting the epic under its own story is a
         * circle as well as the wrong way round, and the circle is the more useful thing to say.
         */
        var subtask = await work.RaiseAsync("Rename the column", who);

        await planning.KindAsync(subtask.Id, WorkItemKind.Subtask);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => planning.ParentAsync(epic.Id, subtask.Id));

        Assert.Contains("cannot sit under", refusal.Message);

        // As is promoting something to a size its parent cannot contain.
        var promoting = await Assert.ThrowsAsync<InvalidOperationException>(
            () => planning.KindAsync(story.Id, WorkItemKind.Epic));

        Assert.Contains("Take it out of its parent", promoting.Message);
    }

    /// <summary>
    /// The hierarchy cannot close a circle.
    /// </summary>
    /// <remarks>
    /// Made by two ordinary moves, neither of which looks wrong on its own, and the result is a
    /// tree walk that never finishes and a breadcrumb that goes round for ever.
    /// </remarks>
    [Fact]
    public async Task Work_cannot_be_put_under_something_below_it()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var (work, planning, rows) = Services(fixture, context);

        var who = await Somebody(fixture, context, "Brian Kiptoo");

        var epic = await work.RaiseAsync("Invoicing", who);
        var feature = await work.RaiseAsync("Payment links", who);
        var story = await work.RaiseAsync("Pay from the email", who);

        await planning.KindAsync(epic.Id, WorkItemKind.Epic);
        await planning.KindAsync(feature.Id, WorkItemKind.Feature);
        await planning.KindAsync(story.Id, WorkItemKind.Story);

        await planning.ParentAsync(feature.Id, epic.Id);
        await planning.ParentAsync(story.Id, feature.Id);

        /*
         * The epic is two levels above the story, so this would close a circle. It is refused for
         * being a loop rather than for the size rule, which would not catch it — an epic under a
         * story fails on size, but this shape can be built out of kinds that are all the right
         * way round.
         */
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => planning.ParentAsync(epic.Id, story.Id));

        Assert.Contains("circle", refusal.Message);

        // And nothing can sit under itself.
        var itself = await Assert.ThrowsAsync<InvalidOperationException>(
            () => planning.ParentAsync(epic.Id, epic.Id));

        Assert.Contains("under itself", itself.Message);
    }

    /// <summary>
    /// A dependency cannot close a circle either.
    /// </summary>
    /// <remarks>
    /// "What can I start now" has no answer when A waits for B and B waits for A, and the pair
    /// looks perfectly reasonable one link at a time.
    /// </remarks>
    [Fact]
    public async Task Dependencies_cannot_make_a_circle()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var (work, planning, rows) = Services(fixture, context);

        var who = await Somebody(fixture, context, "Brian Kiptoo");

        var first = await work.RaiseAsync("Design the schema", who);
        var second = await work.RaiseAsync("Write the endpoint", who);
        var third = await work.RaiseAsync("Wire the screen", who);

        await planning.BlocksAsync(first.Id, second.Id);
        await planning.BlocksAsync(second.Id, third.Id);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => planning.BlocksAsync(third.Id, first.Id));

        Assert.Contains("circle", refusal.Message);

        // Recorded twice is recorded once.
        await planning.BlocksAsync(first.Id, second.Id);

        Assert.Single(await planning.WaitingOnAsync(second.Id));
    }

    /// <summary>
    /// Nothing is done while something it waits on is not.
    /// </summary>
    /// <remarks>
    /// That is what a dependency means. A board that lets the waiting card finish first is
    /// recording an order of events that did not happen, and the fact it records is the one
    /// anybody would later rely on.
    /// </remarks>
    [Fact]
    public async Task Work_cannot_finish_before_what_it_waits_on()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var (work, planning, rows) = Services(fixture, context);

        var who = await Somebody(fixture, context, "Brian Kiptoo");

        var schema = await work.RaiseAsync("Design the schema", who);
        var endpoint = await work.RaiseAsync("Write the endpoint", who);

        await planning.BlocksAsync(schema.Id, endpoint.Id);

        await work.MoveAsync(endpoint.Id, WorkItemStatus.InProgress);
        await work.MoveAsync(endpoint.Id, WorkItemStatus.InReview);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => work.MoveAsync(endpoint.Id, WorkItemStatus.Done));

        Assert.Contains(schema.Reference, refusal.Message);
        Assert.Contains("not finished", refusal.Message);

        await work.MoveAsync(schema.Id, WorkItemStatus.InProgress);
        await work.MoveAsync(schema.Id, WorkItemStatus.InReview);
        await work.MoveAsync(schema.Id, WorkItemStatus.Done);

        await work.MoveAsync(endpoint.Id, WorkItemStatus.Done);

        Assert.Equal(WorkItemStatus.Done, (await rows.FindAsync(endpoint.Id))!.Status);
    }

    /// <summary>
    /// Nothing is done while a line of what done looks like is still false.
    /// </summary>
    /// <remarks>
    /// The rule that makes acceptance criteria worth writing. Without it the list is a decoration
    /// somebody fills in and nobody reads, and the board can call a card finished while the
    /// criteria written on it are plainly not met.
    ///
    /// A line that has turned out not to apply is struck out with a reason rather than ticked, so
    /// the refusal never forces anybody to claim something untrue in order to close a card.
    /// </remarks>
    [Fact]
    public async Task Work_cannot_finish_with_a_criterion_still_false()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var (work, _, rows) = Services(fixture, context);

        var who = await Somebody(fixture, context, "Brian Kiptoo");
        var item = await work.RaiseAsync("Write the endpoint", who);

        await work.NeedsAsync(item.Id, "It returns 201 with the new identifier");
        await work.NeedsAsync(item.Id, "The old endpoint still answers");

        await work.MoveAsync(item.Id, WorkItemStatus.InProgress);
        await work.MoveAsync(item.Id, WorkItemStatus.InReview);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => work.MoveAsync(item.Id, WorkItemStatus.Done));

        Assert.Contains("2 lines of what done looks like", refusal.Message);

        var lines = (await rows.FindAsync(item.Id))!.DoneWhen;

        await work.MetAsync(item.Id, lines[0].Id, who, met: true);

        // One left, and the sentence says so in the singular.
        var again = await Assert.ThrowsAsync<InvalidOperationException>(
            () => work.MoveAsync(item.Id, WorkItemStatus.Done));

        Assert.Contains("One line of what done looks like is not true yet", again.Message);

        // Struck out rather than ticked, because it turned out not to apply.
        await work.DropLineAsync(item.Id, lines[1].Id, "the old endpoint was withdrawn");

        await work.MoveAsync(item.Id, WorkItemStatus.Done);

        Assert.Equal(WorkItemStatus.Done, (await rows.FindAsync(item.Id))!.Status);
    }

    /// <summary>
    /// Labels are reduced, so one word is one label.
    /// </summary>
    /// <remarks>
    /// "Frontend", "frontend" and " FrontEnd " are one label to everybody except a string
    /// comparison, and a board where those are three tags is one where filtering by any of them
    /// hides two thirds of the work.
    /// </remarks>
    [Fact]
    public async Task Labels_are_one_word_however_it_was_typed()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var (work, _, rows) = Services(fixture, context);

        var who = await Somebody(fixture, context, "Brian Kiptoo");
        var item = await work.RaiseAsync("Write the endpoint", who);

        await work.LabelAsync(item.Id, "Frontend");
        await work.LabelAsync(item.Id, " frontend ");
        await work.LabelAsync(item.Id, "needs design");

        var labels = (await rows.FindAsync(item.Id))!.Labels;

        Assert.Equal(2, labels.Count);
        Assert.Contains(labels, one => one.Text == "frontend");
        Assert.Contains(labels, one => one.Text == "needs-design");

        await work.UnlabelAsync(item.Id, "FRONTEND");

        Assert.Single((await rows.FindAsync(item.Id))!.Labels);
    }

    /// <summary>
    /// A comment is edited by whoever wrote it, and the edit is marked.
    /// </summary>
    /// <remarks>
    /// A thread where one person can rewrite another's words is not a record of anything, and an
    /// unmarked edit turns a reply that no longer makes sense into something that reads as
    /// nonsense.
    /// </remarks>
    [Fact]
    public async Task Only_the_author_edits_a_comment_and_the_edit_shows()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var (work, _, rows) = Services(fixture, context);

        var author = await Somebody(fixture, context, "Brian Kiptoo");
        var other = await Somebody(fixture, context, "Grace Wanjiru");
        var item = await work.RaiseAsync("Write the endpoint", author);

        var comment = await work.CommentAsync(item.Id, author, "This needs the schema first.");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => work.RewordAsync(item.Id, comment.Id, other, "Actually it does not."));

        Assert.Contains("whoever wrote it", refusal.Message);

        await work.RewordAsync(item.Id, comment.Id, author, "This needs the schema first, I think.");

        var stored = (await rows.FindAsync(item.Id))!.Comments.Single();

        Assert.True(stored.WasEdited);
        Assert.Contains("I think", stored.Body);
    }

    /// <summary>
    /// The two services and the repository the assertions read through.
    /// </summary>
    /// <remarks>
    /// Reads go through the repository rather than the service, because WorkService writes and
    /// the reading is WorkQueries' job — adding a read to the service for the sake of a test
    /// would be a method no screen calls, which ReachabilityTests fails the build for, correctly.
    /// </remarks>
    /// <summary>
    /// Every kind has a name, and nothing else does.
    /// </summary>
    /// <remarks>
    /// Written after the board showed ten existing cards labelled "subtask" an hour after this
    /// shipped. The migration gave the new column EF's default of zero, which is not a value this
    /// enum has, and the display switch ended in <c>_ =&gt; "subtask"</c> — so it answered
    /// plausibly instead of failing. Two faults, and only the second one is the sort a test can
    /// keep out: the switch now throws, and this asserts both halves of that.
    ///
    /// The first half — the backfill — was fixed in the migration and cannot be tested here,
    /// because by the time a test has an entity the column has a real value. It is written down
    /// in the migration instead.
    /// </remarks>
    [Fact]
    public void Every_kind_of_work_has_a_name_and_nothing_else_does()
    {
        foreach (var kind in Enum.GetValues<WorkItemKind>())
        {
            Assert.False(string.IsNullOrWhiteSpace(WorkItem.Name(kind)));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => WorkItem.Name(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkItem.Name((WorkItemKind)99));
    }

    private static (WorkService Work, PlanningService Planning, WorkRepository Rows) Services(
        DatabaseFixture fixture, TestDbContext context)
    {
        var work = new WorkRepository(context);
        var people = new PeopleRepository(context);
        var planning = new PlanningRepository(context);

        return (
            new WorkService(work, people, planning, fixture.Clock),
            new PlanningService(planning, work, fixture.Clock),
            work);
    }

    private static async Task<Guid> Somebody(
        DatabaseFixture fixture, TestDbContext context, string name)
    {
        var employee = Employee.Hire(name, fixture.Clock.Today, null, "Engineer");

        employee.Start();

        context.Employees.Add(employee);
        await context.SaveChangesAsync();

        return employee.Id;
    }
}
