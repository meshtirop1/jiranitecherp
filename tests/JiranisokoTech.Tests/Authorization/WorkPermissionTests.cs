using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Application.Work;
using JiranisokoTech.Domain.Work;

namespace JiranisokoTech.Tests.Authorization;

/// <summary>
/// Who may move a piece of work, and where to.
/// </summary>
/// <remarks>
/// Written after finding that tasks.update_own, tasks.submit and tasks.review
/// were declared, granted, matrix-tested and checked by nothing, while the work
/// item page put every transition behind tasks.assign. An engineer could open
/// their own work and not start it.
/// </remarks>
public class WorkPermissionTests
{
    private static readonly Guid Duncan = Guid.CreateVersion7();
    private static readonly Guid Somebody = Guid.CreateVersion7();

    private static HashSet<string> HeldBy(string role) => [.. Roles.PermissionsFor(role)];

    /// <summary>
    /// A developer can start and submit the work assigned to them.
    /// </summary>
    /// <remarks>
    /// The case the whole thing exists for. If this fails, the board is
    /// readable and unusable by the people it is for.
    /// </remarks>
    [Fact]
    public void A_developer_can_move_their_own_work_along()
    {
        var developer = HeldBy(Roles.Developer);

        Assert.True(WorkPermissions.MayMove(
            developer, WorkItemStatus.InProgress, Duncan, Duncan));

        Assert.True(WorkPermissions.MayMove(
            developer, WorkItemStatus.InReview, Duncan, Duncan));

        Assert.True(WorkPermissions.MayMove(
            developer, WorkItemStatus.Blocked, Duncan, Duncan));
    }

    /// <summary>And not somebody else's.</summary>
    /// <remarks>
    /// The "own" in tasks.update_own is the whole of it. Without this the
    /// permission means "update anything", which is a different permission that
    /// already exists and is called tasks.assign.
    /// </remarks>
    [Fact]
    public void A_developer_cannot_move_somebody_elses_work()
    {
        var developer = HeldBy(Roles.Developer);

        Assert.False(WorkPermissions.MayMove(
            developer, WorkItemStatus.InProgress, Somebody, Duncan));

        Assert.False(WorkPermissions.MayMove(
            developer, WorkItemStatus.InReview, Somebody, Duncan));
    }

    /// <summary>
    /// Unassigned work is nobody's, including somebody with no staff record.
    /// </summary>
    /// <remarks>
    /// null equals null, and this system has already shipped that hole once on
    /// the work item page. Both sides have to be a real person.
    /// </remarks>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Nobody_owns_unassigned_work(bool assigned, bool known)
    {
        var developer = HeldBy(Roles.Developer);

        Assert.False(WorkPermissions.MayMove(
            developer,
            WorkItemStatus.InProgress,
            assigned ? Duncan : null,
            known ? Duncan : null));
    }

    /// <summary>
    /// A developer cannot accept their own work, or cancel anything.
    /// </summary>
    /// <remarks>
    /// Accepting is the reviewer's act and cancelling is the board-runner's.
    /// Letting the person who did the work mark it done removes the point of
    /// having a review state at all.
    /// </remarks>
    [Fact]
    public void A_developer_cannot_accept_or_cancel()
    {
        var developer = HeldBy(Roles.Developer);

        Assert.False(WorkPermissions.MayMove(developer, WorkItemStatus.Done, Duncan, Duncan));
        Assert.False(WorkPermissions.MayMove(developer, WorkItemStatus.Cancelled, Duncan, Duncan));
    }

    /// <summary>
    /// A reviewer accepts work that is by definition not theirs.
    /// </summary>
    [Fact]
    public void A_reviewer_accepts_work_that_is_not_theirs()
    {
        var head = HeldBy(Roles.DepartmentHead);

        Assert.True(WorkPermissions.MayMove(head, WorkItemStatus.Done, Somebody, Duncan));
    }

    /// <summary>Running the board means moving anything on it, except releasing it.</summary>
    /// <remarks>
    /// The release is the one exception, and it is the reason the gate is worth
    /// anything at all. A delivery manager holds tasks.assign, so a blanket
    /// "anybody who runs the board may make any move" would wave them through
    /// before tasks.deploy was ever read — and the permission would exist, be
    /// granted to one role, be asserted by the matrix tests, and guard nothing.
    /// </remarks>
    [Fact]
    public void Somebody_who_runs_the_board_may_move_anything_except_release_it()
    {
        var manager = HeldBy(Roles.ProjectManager);

        foreach (var target in Enum.GetValues<WorkItemStatus>()
            .Where(target => target != WorkItemStatus.Deployed))
        {
            Assert.True(
                WorkPermissions.MayMove(manager, target, Somebody, Duncan),
                $"A manager could not move work to {target}.");
        }

        Assert.False(WorkPermissions.MayMove(
            manager, WorkItemStatus.Deployed, Somebody, Duncan));
    }

    /// <summary>
    /// The release is the head of department's, and the state machine will not
    /// move without the permission.
    /// </summary>
    /// <remarks>
    /// The capability this replaces: the system being retired has a head who
    /// releases what their team finishes. It was declared here once as
    /// tasks.deploy, named a state the work machine did not have, and was
    /// deleted rather than invented around.
    /// </remarks>
    [Fact]
    public void A_department_head_releases_accepted_work()
    {
        var head = HeldBy(Roles.DepartmentHead);

        Assert.Contains(Permissions.TasksDeploy, head);
        Assert.Equal(Permissions.TasksDeploy, WorkPermissions.Governing(WorkItemStatus.Deployed));

        // Not their own work, any more than a review is: the person who released
        // it is not the person who did it.
        Assert.True(WorkPermissions.MayMove(head, WorkItemStatus.Deployed, Somebody, Duncan));
    }

    /// <summary>A developer cannot release their own work, or anybody's.</summary>
    /// <remarks>
    /// Being the person who did the work is what makes this one obvious, and it
    /// is the case the gate exists for: accepting work and releasing it are two
    /// decisions, and neither of them is the author's.
    /// </remarks>
    [Fact]
    public void A_developer_cannot_release_anything()
    {
        var developer = HeldBy(Roles.Developer);

        Assert.DoesNotContain(Permissions.TasksDeploy, developer);

        Assert.False(WorkPermissions.MayMove(developer, WorkItemStatus.Deployed, Duncan, Duncan));
        Assert.False(WorkPermissions.MayMove(developer, WorkItemStatus.Deployed, Somebody, Duncan));

        // Nor is it offered to them from the one state it is reachable from, so
        // the button is not drawn and then refused.
        Assert.DoesNotContain(
            WorkItemStatus.Deployed,
            WorkPermissions.MovesFor(developer, WorkItemStatus.Done, Duncan, Duncan));
    }

    /// <summary>
    /// The release is offered to a head only from accepted work.
    /// </summary>
    /// <remarks>
    /// Both filters again: holding tasks.deploy is not a button on every card,
    /// because the state machine allows the move from Done and nowhere else.
    /// </remarks>
    [Theory]
    [InlineData(WorkItemStatus.Done, true)]
    [InlineData(WorkItemStatus.InReview, false)]
    [InlineData(WorkItemStatus.InProgress, false)]
    [InlineData(WorkItemStatus.Todo, false)]
    [InlineData(WorkItemStatus.Blocked, false)]
    public void Releasing_is_offered_from_accepted_work_only(WorkItemStatus from, bool offered)
    {
        var head = HeldBy(Roles.DepartmentHead);

        var moves = WorkPermissions.MovesFor(head, from, Somebody, Duncan);

        Assert.Equal(offered, moves.Contains(WorkItemStatus.Deployed));
    }

    /// <summary>
    /// The buttons offered are the moves the state machine allows, narrowed to
    /// the ones this person may make.
    /// </summary>
    /// <remarks>
    /// Both filters, and in that order. Offering a move the state machine
    /// refuses and offering one the person may not make are the same failure to
    /// somebody clicking it.
    /// </remarks>
    [Fact]
    public void The_moves_offered_are_possible_and_permitted()
    {
        var developer = HeldBy(Roles.Developer);

        var offered = WorkPermissions.MovesFor(
            developer, WorkItemStatus.InReview, Duncan, Duncan);

        // From review the machine allows Done, InProgress, Blocked, Cancelled.
        // A developer may send it back and block it, and may do neither of the
        // other two.
        Assert.Contains(WorkItemStatus.InProgress, offered);
        Assert.Contains(WorkItemStatus.Blocked, offered);
        Assert.DoesNotContain(WorkItemStatus.Done, offered);
        Assert.DoesNotContain(WorkItemStatus.Cancelled, offered);

        Assert.All(
            offered,
            target => Assert.Contains(target, WorkItem.NextFrom(WorkItemStatus.InReview)));
    }

    [Fact]
    public void Somebody_holding_nothing_can_move_nothing()
    {
        var nobody = new HashSet<string>();

        Assert.Empty(WorkPermissions.MovesFor(nobody, WorkItemStatus.Todo, Duncan, Duncan));
    }
}
