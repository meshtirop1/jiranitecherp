using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Domain.Work;

namespace JiranisokoTech.Application.Work;

/// <summary>
/// Who may move a piece of work, and where to.
/// </summary>
/// <remarks>
/// This exists because the permissions were there and the enforcement was not.
/// tasks.update_own, tasks.submit and tasks.review were declared, granted to
/// roles, covered by the role-matrix tests, and checked by nothing — while the
/// work item page put every transition behind tasks.assign. The effect was that
/// an engineer could open their own work and not move it: not start it, not
/// submit it. The same shape as the board they could not open, one layer along.
///
/// A plain function rather than an authorization handler, so it can be tested
/// without a browser, called from the page to decide which buttons to draw, and
/// called again by the service that performs the move. Drawing and doing must
/// agree, and the only way to be sure is that they ask the same code.
///
/// Two things decide it: which transition, and whether the work is yours.
/// "Yours" is the assignee, not the person who raised it — the one doing the
/// work is the one who says it has started.
/// </remarks>
public static class WorkPermissions
{
    /// <summary>
    /// The permission that governs a move, ignoring whose work it is.
    /// </summary>
    /// <remarks>
    /// Cancelling is deliberately the manager's permission rather than the
    /// owner's. Cancelling work is a decision about whether the firm still
    /// wants it, which is not the same question as whether the person holding
    /// it wants to stop.
    /// </remarks>
    public static string Governing(WorkItemStatus target) => target switch
    {
        // Accepting finished work, or sending it back, is the reviewer's act.
        WorkItemStatus.Done => Permissions.TasksReview,

        // Putting it up for review is the worker's.
        WorkItemStatus.InReview => Permissions.TasksSubmit,

        WorkItemStatus.Cancelled => Permissions.TasksAssign,

        // Starting, stopping, putting it back, saying it is blocked.
        _ => Permissions.TasksUpdateOwn,
    };

    /// <summary>
    /// May this person make this move?
    /// </summary>
    /// <remarks>
    /// Somebody who runs the board — tasks.assign — may move anything, because
    /// that is what running a board is. Everybody else needs the governing
    /// permission <em>and</em> the work has to be theirs; a permission to update
    /// your own work is not a permission to update somebody else's, and the
    /// "own" in the name is the whole of it.
    ///
    /// Review is the exception that proves it: a reviewer is by definition not
    /// the person who did the work, so it does not ask whose it is.
    /// </remarks>
    public static bool MayMove(
        IReadOnlySet<string> permissions,
        WorkItemStatus target,
        Guid? assigneeId,
        Guid? employeeId)
    {
        if (permissions.Contains(Permissions.TasksAssign))
        {
            return true;
        }

        var governing = Governing(target);

        if (!permissions.Contains(governing))
        {
            return false;
        }

        if (governing == Permissions.TasksReview)
        {
            return true;
        }

        // Both sides have to be somebody. Without this an unassigned item reads
        // as "mine" to anybody with no staff record — which is null == null,
        // and is a hole this system has already had once.
        return assigneeId is not null && employeeId is not null && assigneeId == employeeId;
    }

    /// <summary>The moves worth drawing buttons for.</summary>
    /// <remarks>
    /// The state machine says which are possible; this narrows them to the ones
    /// this person may actually make. A button that is drawn and then refused
    /// teaches people to distrust every button on the page.
    /// </remarks>
    public static IReadOnlyList<WorkItemStatus> MovesFor(
        IReadOnlySet<string> permissions,
        WorkItemStatus from,
        Guid? assigneeId,
        Guid? employeeId) =>
        [.. WorkItem.NextFrom(from)
            .Where(target => MayMove(permissions, target, assigneeId, employeeId))];
}
