using JiranisokoTech.Domain.Work;

namespace JiranisokoTech.Tests.Domain;

/// <summary>
/// The state machine, which is the whole point of the class.
/// </summary>
/// <remarks>
/// A settable status column is how work gets marked done without review,
/// cancelled and quietly resumed, or blocked with no note of what is blocking
/// it. Each of those looks perfectly fine in the database and is a lie on the
/// board.
/// </remarks>
public class WorkItemTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    private static WorkItem Raised(string title = "Ship the delivery note printer") =>
        WorkItem.Raise(title, Guid.CreateVersion7());

    private static WorkItem At(WorkItemStatus status)
    {
        var item = Raised();

        foreach (var step in Path(status))
        {
            item.MoveTo(step, Now, step == WorkItemStatus.Blocked ? "waiting on the client" : null);
        }

        item.ClearEvents();

        return item;
    }

    private static IEnumerable<WorkItemStatus> Path(WorkItemStatus status) => status switch
    {
        WorkItemStatus.Todo => [],
        WorkItemStatus.InProgress => [WorkItemStatus.InProgress],
        WorkItemStatus.InReview => [WorkItemStatus.InProgress, WorkItemStatus.InReview],
        WorkItemStatus.Blocked => [WorkItemStatus.Blocked],
        WorkItemStatus.Done =>
            [WorkItemStatus.InProgress, WorkItemStatus.InReview, WorkItemStatus.Done],
        WorkItemStatus.Cancelled => [WorkItemStatus.Cancelled],
        WorkItemStatus.Deployed =>
        [
            WorkItemStatus.InProgress, WorkItemStatus.InReview, WorkItemStatus.Done,
            WorkItemStatus.Deployed,
        ],
        _ => [],
    };

    [Fact]
    public void Work_starts_on_the_list_and_nowhere_else()
    {
        var item = WorkItem.Raise("  Ship it  ", Guid.CreateVersion7());

        Assert.Equal("Ship it", item.Title);
        Assert.Equal(WorkItemStatus.Todo, item.Status);
        Assert.Equal(Priority.Normal, item.Priority);
        Assert.True(item.IsOpen);
        Assert.Null(item.AssigneeId);
        Assert.Single(item.Events.OfType<WorkItemRaised>());
    }

    [Fact]
    public void A_title_is_required()
    {
        Assert.Throws<ArgumentException>(() => WorkItem.Raise("   ", Guid.CreateVersion7()));
    }

    [Theory]
    [InlineData(WorkItemStatus.Todo, WorkItemStatus.InProgress)]
    [InlineData(WorkItemStatus.InProgress, WorkItemStatus.InReview)]
    [InlineData(WorkItemStatus.InReview, WorkItemStatus.Done)]
    [InlineData(WorkItemStatus.InReview, WorkItemStatus.InProgress)]
    [InlineData(WorkItemStatus.Blocked, WorkItemStatus.InProgress)]
    [InlineData(WorkItemStatus.Done, WorkItemStatus.InProgress)]
    [InlineData(WorkItemStatus.Done, WorkItemStatus.Deployed)]
    public void An_allowed_move_is_made(WorkItemStatus from, WorkItemStatus to)
    {
        var item = At(from);

        item.MoveTo(to, Now);

        Assert.Equal(to, item.Status);
        Assert.Single(item.Events.OfType<WorkItemMoved>());
    }

    /// <summary>
    /// The moves that are missing from the table are the interesting ones.
    /// </summary>
    [Theory]
    [InlineData(WorkItemStatus.Todo, WorkItemStatus.Done)]
    [InlineData(WorkItemStatus.Todo, WorkItemStatus.InReview)]
    [InlineData(WorkItemStatus.InProgress, WorkItemStatus.Done)]
    [InlineData(WorkItemStatus.Blocked, WorkItemStatus.Done)]
    public void Work_cannot_skip_review(WorkItemStatus from, WorkItemStatus to)
    {
        var item = At(from);

        var refused = Assert.Throws<InvalidOperationException>(() => item.MoveTo(to, Now));

        Assert.Contains("cannot go from", refused.Message);
        Assert.Equal(from, item.Status);
    }

    /// <summary>
    /// Nothing is released that has not been accepted first.
    /// </summary>
    /// <remarks>
    /// Done is the only way in. A release straight from in progress is a release
    /// nobody reviewed, and the state exists precisely so that the two decisions
    /// are made by two people.
    /// </remarks>
    [Theory]
    [InlineData(WorkItemStatus.Todo)]
    [InlineData(WorkItemStatus.InProgress)]
    [InlineData(WorkItemStatus.InReview)]
    [InlineData(WorkItemStatus.Blocked)]
    public void Nothing_is_released_before_it_is_accepted(WorkItemStatus from)
    {
        var item = At(from);

        var refused = Assert.Throws<InvalidOperationException>(
            () => item.MoveTo(WorkItemStatus.Deployed, Now));

        Assert.Contains("cannot go from", refused.Message);
        Assert.Equal(from, item.Status);
    }

    /// <summary>
    /// A release is a statement about the world outside the firm, so the row
    /// stands.
    /// </summary>
    /// <remarks>
    /// Moving it back would leave the system claiming a release never happened
    /// while the release is still out there. A fault found afterwards, or a
    /// rollback, is new work with its own reason — the same rule as cancelled
    /// work, reached from the opposite direction.
    /// </remarks>
    [Theory]
    [InlineData(WorkItemStatus.InProgress)]
    [InlineData(WorkItemStatus.Todo)]
    [InlineData(WorkItemStatus.Done)]
    [InlineData(WorkItemStatus.Cancelled)]
    public void Nothing_comes_back_from_deployed(WorkItemStatus to)
    {
        var item = At(WorkItemStatus.Deployed);

        var refused = Assert.Throws<InvalidOperationException>(() => item.MoveTo(to, Now));

        Assert.Contains("stays deployed", refused.Message);
        Assert.Equal(WorkItemStatus.Deployed, item.Status);
    }

    /// <summary>
    /// Released work is finished work, and the reporting figures read this.
    /// </summary>
    /// <remarks>
    /// Every count of open work asks this question, in SQL, in five places. If a
    /// released item reads as open, the firm is told it has more work in flight
    /// than it has and that things it shipped are overdue.
    /// </remarks>
    [Fact]
    public void Released_work_is_not_open_and_keeps_the_date_it_was_accepted()
    {
        var item = Raised();

        item.MoveTo(WorkItemStatus.InProgress, Now);
        item.MoveTo(WorkItemStatus.InReview, Now);
        item.MoveTo(WorkItemStatus.Done, Now.AddDays(1));
        item.MoveTo(WorkItemStatus.Deployed, Now.AddDays(4));

        Assert.False(item.IsOpen);
        Assert.Contains(WorkItemStatus.Deployed, WorkItem.Finished);

        // The release does not overwrite when the work was accepted: that is the
        // date the delivery figures are worked out from.
        Assert.Equal(Now.AddDays(1), item.CompletedAt);
    }

    /// <summary>
    /// Work picked up again is new work with a new decision behind it. Reviving
    /// the old row silently rewrites why it was dropped.
    /// </summary>
    [Theory]
    [InlineData(WorkItemStatus.Todo)]
    [InlineData(WorkItemStatus.InProgress)]
    [InlineData(WorkItemStatus.Done)]
    [InlineData(WorkItemStatus.Deployed)]
    public void Nothing_comes_back_from_cancelled(WorkItemStatus to)
    {
        var item = At(WorkItemStatus.Cancelled);

        var refused = Assert.Throws<InvalidOperationException>(() => item.MoveTo(to, Now));

        Assert.Contains("stays cancelled", refused.Message);
    }

    /// <summary>
    /// A block with no reason is work nobody can unblock, because nobody knows
    /// what is being waited for.
    /// </summary>
    [Fact]
    public void Blocking_without_saying_why_is_refused()
    {
        var item = Raised();

        Assert.Throws<ArgumentException>(() => item.MoveTo(WorkItemStatus.Blocked, Now));
        Assert.Equal(WorkItemStatus.Todo, item.Status);
    }

    [Fact]
    public void A_block_carries_its_reason_and_loses_it_on_the_way_out()
    {
        var item = Raised();

        item.MoveTo(WorkItemStatus.Blocked, Now, "  waiting on the client  ");
        Assert.Equal("waiting on the client", item.BlockedReason);

        item.MoveTo(WorkItemStatus.InProgress, Now);
        Assert.Null(item.BlockedReason);
    }

    [Fact]
    public void Finishing_records_when_and_announces_it_on_its_own()
    {
        var item = At(WorkItemStatus.InReview);

        item.MoveTo(WorkItemStatus.Done, Now);

        Assert.Equal(Now, item.CompletedAt);
        Assert.False(item.IsOpen);

        // Its own event, because far more things care about this transition than
        // about any other, and making them all filter an enum is how a
        // subscriber ends up reacting to the wrong one.
        Assert.Single(item.Events.OfType<WorkItemCompleted>());
    }

    /// <summary>
    /// Reopening finished work must not rewrite when it was first picked up, and
    /// must clear the completion it no longer has.
    /// </summary>
    [Fact]
    public void Reopening_keeps_the_original_start_and_drops_the_finish()
    {
        var item = Raised();

        item.MoveTo(WorkItemStatus.InProgress, Now);
        var started = item.StartedAt;

        item.MoveTo(WorkItemStatus.InReview, Now);
        item.MoveTo(WorkItemStatus.Done, Now.AddDays(1));
        item.MoveTo(WorkItemStatus.InProgress, Now.AddDays(2));

        Assert.Equal(started, item.StartedAt);
        Assert.Null(item.CompletedAt);
    }

    [Fact]
    public void Moving_to_where_it_already_is_changes_nothing()
    {
        var item = At(WorkItemStatus.InProgress);

        item.MoveTo(WorkItemStatus.InProgress, Now);

        Assert.Empty(item.Events);
    }

    [Fact]
    public void Assigning_says_who_lost_it_as_well_as_who_gained_it()
    {
        var item = Raised();
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        item.AssignTo(first);
        item.AssignTo(second);

        var moves = item.Events.OfType<WorkItemAssigned>().ToList();

        Assert.Equal(2, moves.Count);
        Assert.Null(moves[0].FromEmployeeId);

        // The person losing it needs telling as much as the person gaining it:
        // work that quietly left somebody's list is the commonest way it is
        // dropped.
        Assert.Equal(first, moves[1].FromEmployeeId);
        Assert.Equal(second, moves[1].ToEmployeeId);
    }

    [Fact]
    public void Finished_work_cannot_be_handed_to_somebody_new()
    {
        var item = At(WorkItemStatus.Done);

        Assert.Throws<InvalidOperationException>(() => item.AssignTo(Guid.CreateVersion7()));
    }

    [Fact]
    public void Finished_work_can_still_be_taken_off_somebody()
    {
        var item = Raised();
        item.AssignTo(Guid.CreateVersion7());
        item.MoveTo(WorkItemStatus.InProgress, Now);
        item.MoveTo(WorkItemStatus.InReview, Now);
        item.MoveTo(WorkItemStatus.Done, Now);

        item.AssignTo(null);

        Assert.Null(item.AssigneeId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(4800)]
    public void An_estimate_up_to_a_fortnight_is_accepted(int minutes)
    {
        var item = Raised();

        item.Estimate(minutes);

        Assert.Equal(minutes, item.EstimateMinutes);
    }

    /// <summary>
    /// Anything larger is a project that has not been broken up, and letting it
    /// through means a board where one card hides a month of work.
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(4801)]
    public void An_estimate_beyond_a_fortnight_is_refused(int minutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Raised().Estimate(minutes));
    }

    [Fact]
    public void An_estimate_can_be_taken_away_again()
    {
        var item = Raised();
        item.Estimate(120);

        item.Estimate(null);

        Assert.Null(item.EstimateMinutes);
    }
}
