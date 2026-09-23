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

    /// <summary>Running the board means moving anything on it.</summary>
    [Fact]
    public void Somebody_who_runs_the_board_may_move_anything()
    {
        var manager = HeldBy(Roles.ProjectManager);

        foreach (var target in Enum.GetValues<WorkItemStatus>())
        {
            Assert.True(
                WorkPermissions.MayMove(manager, target, Somebody, Duncan),
                $"A manager could not move work to {target}.");
        }
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
