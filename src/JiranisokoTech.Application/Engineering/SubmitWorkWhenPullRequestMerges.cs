using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Work;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Domain.Work;
using Microsoft.Extensions.Logging;

namespace JiranisokoTech.Application.Engineering;

/// <summary>
/// Work whose pull request has merged puts itself up for review.
/// </summary>
/// <remarks>
/// This is the brief's first principle made concrete, and the whole reason the
/// repositories are watched at all: an engineer who has merged their branch has
/// finished the part that is theirs, and should not then have to go and tell a
/// board what the repository already knows.
///
/// The automation stops exactly there, and where it stops is the decision worth
/// explaining. Merged means the code is in; it does not mean anybody has looked
/// at whether the work was right, and it certainly does not mean it has been
/// released. So this moves work to review and no further. Accepting it stays
/// tasks.review, releasing it stays tasks.deploy, and both stay a person's act.
/// A system that marked work done on a merge would be quietly deciding, on
/// everybody's behalf, that merging and accepting are the same event — and the
/// board would fill with finished work nobody had agreed was finished.
///
/// Only work that is genuinely in progress is moved. Something already in
/// review, done, released or cancelled is left alone: a late or replayed
/// delivery must not drag accepted work backwards, and a merge onto a branch for
/// work somebody cancelled is not a reason to reopen it.
/// </remarks>
public sealed class SubmitWorkWhenPullRequestMerges(
    IWorkRepository work,
    IClock clock,
    ILogger<SubmitWorkWhenPullRequestMerges> logger)
    : IDomainEventHandler<PullRequestMerged>
{
    public async Task HandleAsync(
        PullRequestMerged domainEvent, CancellationToken cancellationToken = default)
    {
        if (domainEvent.WorkItemId is not { } workItemId)
        {
            // Nobody wrote a reference. Normal, and not this handler's problem
            // to solve — the pull request is recorded and somebody can attach
            // it to the work by hand.
            return;
        }

        var item = await work.FindAsync(workItemId, cancellationToken);

        if (item is null || item.Status != WorkItemStatus.InProgress)
        {
            /*
             * The idempotency, and it is free: the second delivery of the same
             * merge finds the item already in review and does nothing. Nothing
             * needs to remember that this ran.
             */
            return;
        }

        item.MoveTo(
            WorkItemStatus.InReview,
            clock.Now,
            $"Pull request #{domainEvent.Number} was merged.");

        await work.SaveAsync(cancellationToken);

        logger.LogInformation(
            "Work item {Number} moved to review because pull request #{PullRequest} merged.",
            item.Reference,
            domainEvent.Number);
    }
}
