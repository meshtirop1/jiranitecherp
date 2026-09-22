using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Approvals;
using JiranisokoTech.Domain.Approvals;
using JiranisokoTech.Domain.Recruitment;
using Microsoft.Extensions.Logging;

namespace JiranisokoTech.Application.Recruitment;

/// <summary>
/// What a submitted requisition is worth to the approval engine.
/// </summary>
/// <remarks>
/// Named here, in recruitment, because it is a hiring rule and not an approval
/// rule: a hire is agreed by the people the asker answers to, two levels up.
/// The approval engine knows how to run a chain and nothing about what is being
/// decided.
/// </remarks>
public static class RequisitionApproval
{
    /// <summary>The action recorded on the chain, and matched on the way back.</summary>
    public const string Action = "requisition.open";

    public const string Subject = nameof(JobRequisition);

    /// <summary>
    /// How far up a hire goes.
    /// </summary>
    /// <remarks>
    /// Two: the asker's manager, and theirs. One is a rubber stamp from somebody
    /// with the same budget pressure; three, in a firm of this size, means the
    /// managing director signs off every junior engineer and stops reading them.
    /// </remarks>
    public const int Levels = 2;
}

/// <summary>
/// Opens the chain when a requisition is submitted.
/// </summary>
/// <remarks>
/// Runs from the outbox, so it retries if the roster is briefly unreachable and
/// gives up rather than looping. It is idempotent because the approval service
/// refuses a second live chain over the same decision — so a message delivered
/// twice opens one chain and logs the second attempt.
/// </remarks>
public sealed class OpenTheChainWhenARequisitionIsSubmitted(
    ApprovalService approvals,
    ILogger<OpenTheChainWhenARequisitionIsSubmitted> logger)
    : IDomainEventHandler<RequisitionSubmitted>
{
    public async Task HandleAsync(
        RequisitionSubmitted domainEvent, CancellationToken cancellationToken = default)
    {
        try
        {
            await approvals.RequestUpTheLineAsync(
                RequisitionApproval.Subject,
                domainEvent.RequisitionId,
                RequisitionApproval.Action,
                domainEvent.RaisedById,
                RequisitionApproval.Levels,
                cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            /*
             * Logged and let go rather than retried.
             *
             * The two ways this fails are somebody at the top of the firm with
             * nobody above them, and a chain already open. Neither is fixed by
             * trying again in thirty seconds, and both need a person: either to
             * name an approver, or to notice that the first chain is still
             * running.
             *
             * The requisition stays at awaiting-approval, which is where the
             * screen shows it, so nothing is silently lost.
             */
            logger.LogWarning(
                exception,
                "Could not open an approval chain for requisition {RequisitionId} ({Title}).",
                domainEvent.RequisitionId,
                domainEvent.JobTitle);
        }
    }
}

/// <summary>
/// Tells the requisition what the approvers decided.
/// </summary>
/// <remarks>
/// The other half of the join. Recruitment never asks the approval engine
/// anything; it is told, once, and the two modules share nothing but an action
/// name and an identifier.
/// </remarks>
public sealed class TellTheRequisitionWhatWasDecided(
    RecruitmentService recruitment,
    ILogger<TellTheRequisitionWhatWasDecided> logger)
    : IDomainEventHandler<ApprovalSettled>
{
    public async Task HandleAsync(
        ApprovalSettled domainEvent, CancellationToken cancellationToken = default)
    {
        // Every settled chain in the system arrives here. Anything that is not a
        // hiring decision belongs to somebody else.
        if (domainEvent.Action != RequisitionApproval.Action
            || domainEvent.SubjectType != RequisitionApproval.Subject)
        {
            return;
        }

        // A withdrawn chain leaves the requisition where it was. Whoever
        // withdrew it is the person who raised it, and they can submit again.
        if (domainEvent.Status == ApprovalStatus.Withdrawn)
        {
            return;
        }

        try
        {
            await recruitment.RecordDecisionAsync(
                domainEvent.SubjectId,
                domainEvent.Status == ApprovalStatus.Approved,
                domainEvent.Outcome,
                cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            // The requisition was closed or decided while the chain was still
            // running. Not retryable, and not a fault: the decision on the
            // record is the one that was made first.
            logger.LogWarning(
                exception,
                "Approval {ApprovalId} settled but requisition {RequisitionId} would not take it.",
                domainEvent.ApprovalId,
                domainEvent.SubjectId);
        }
    }
}
