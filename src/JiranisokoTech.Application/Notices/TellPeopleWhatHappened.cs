using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.Approvals;
using JiranisokoTech.Domain.Notices;
using JiranisokoTech.Domain.Work;

namespace JiranisokoTech.Application.Notices;

/// <summary>
/// Somebody was given a piece of work, or lost one.
/// </summary>
/// <remarks>
/// Both ends, because the event carries both and because work quietly leaving somebody's list
/// is the most common way it gets dropped — the person losing it needs telling as much as the
/// person gaining it, which is exactly what the remark on <see cref="WorkItemAssigned"/> says.
///
/// Unassigning is not a reassignment: when a piece of work is taken off somebody and given to
/// nobody, only one notice goes out. Sending a "you were given" to nobody is not possible, and
/// sending one to the person who just lost it would be a second notice saying the same thing.
/// </remarks>
public sealed class TellPeopleTheirWorkMoved(NoticeService notices)
    : IDomainEventHandler<WorkItemAssigned>
{
    public async Task HandleAsync(
        WorkItemAssigned domainEvent, CancellationToken cancellationToken = default)
    {
        if (domainEvent.ToEmployeeId is { } given)
        {
            await notices.TellAsync(
                given,
                NoticeKind.WorkGiven,
                $"You were given \"{domainEvent.Title}\".",
                $"/work/{domainEvent.WorkItemId}",
                cancellationToken);
        }

        if (domainEvent.FromEmployeeId is { } taken && taken != domainEvent.ToEmployeeId)
        {
            await notices.TellAsync(
                taken,
                NoticeKind.WorkTaken,
                domainEvent.ToEmployeeId is null
                    ? $"\"{domainEvent.Title}\" is no longer assigned to you."
                    : $"\"{domainEvent.Title}\" has gone to somebody else.",
                $"/work/{domainEvent.WorkItemId}",
                cancellationToken);
        }
    }
}

/// <summary>
/// Something somebody asked for has been decided.
/// </summary>
/// <remarks>
/// To whoever raised it, which the event carries on purpose rather than leaving to be looked up
/// — a handler running from the outbox may be doing this minutes later, by which time the
/// request it would have read could have been swept or changed.
///
/// <b>The outcome is in the sentence and nothing else is.</b> An approval's outcome is somebody
/// explaining a refusal to the person refused, and there is no version of that which belongs in
/// a shortened summary — so the notice says it was approved or refused and links to the thing,
/// where the words are.
/// </remarks>
public sealed class TellSomebodyTheirRequestWasSettled(NoticeService notices)
    : IDomainEventHandler<ApprovalSettled>
{
    public Task HandleAsync(
        ApprovalSettled domainEvent, CancellationToken cancellationToken = default) =>
        notices.TellAsync(
            domainEvent.RequestedById,
            NoticeKind.RequestSettled,
            domainEvent.Status == ApprovalStatus.Approved
                ? $"Your {Said(domainEvent.Action)} was approved."
                : $"Your {Said(domainEvent.Action)} was refused.",
            "/approvals",
            cancellationToken);

    /// <summary>
    /// The action, in words a person would use.
    /// </summary>
    /// <remarks>
    /// The action is a machine-readable string like <c>requisition.approve</c>, and putting
    /// that in front of somebody is how a notice reads as a log line. Anything unrecognised
    /// falls back to "request", which is always true and never wrong.
    /// </remarks>
    private static string Said(string action) => action switch
    {
        "requisition.approve" => "request to recruit",
        "leave.approve" => "leave request",
        "expense.approve" => "expense claim",
        "timesheet.approve" => "timesheet",
        _ => "request",
    };
}
