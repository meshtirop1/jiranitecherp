using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Observability;
using JiranisokoTech.Application.Work;
using JiranisokoTech.Domain.Engineering;
using Microsoft.Extensions.Logging;

namespace JiranisokoTech.Application.Engineering;

/// <summary>
/// Turns recorded deliveries into commits, pull requests and reviews.
/// </summary>
/// <remarks>
/// The half of the integration that is allowed to be slow, and allowed to fail.
/// <see cref="WebhookInbox"/> has already answered the provider, so nothing here
/// is racing a ten-second timeout, and a delivery that throws is marked failed
/// and tried again rather than lost.
///
/// Only the work is here; the loop that calls it lives in the infrastructure, so
/// that a test can run exactly one pass and assert on what happened. A dispatcher
/// that contains its own timer can only be tested by starting it and waiting.
/// </remarks>
public sealed class DeliveryDispatcher(
    IEngineeringRepository repositories,
    IWorkRepository work,
    IEnumerable<IGitProvider> adapters,
    IClock clock,
    ILogger<DeliveryDispatcher> logger)
{
    /// <summary>Handle one batch. Returns how many were attempted.</summary>
    public async Task<int> RunOnceAsync(
        int batchSize = 50, CancellationToken cancellationToken = default)
    {
        var waiting = await repositories.WaitingDeliveriesAsync(batchSize, cancellationToken);

        foreach (var delivery in waiting)
        {
            await HandleAsync(delivery, cancellationToken);
        }

        return waiting.Count;
    }

    private async Task HandleAsync(
        WebhookDelivery delivery, CancellationToken cancellationToken)
    {
        try
        {
            var adapter = adapters.FirstOrDefault(one => one.Provider == delivery.Provider)
                ?? throw new InvalidOperationException(
                    $"Nothing here reads {delivery.Provider} payloads.");

            if (delivery.RepositoryId is not { } repositoryId)
            {
                delivery.Ignored("No repository is connected for this delivery.", clock.Now);
            }
            else
            {
                var read = adapter.Read(delivery.Event, delivery.Payload);

                switch (read)
                {
                    case GitEvent.Pushed pushed:
                        await RecordAsync(repositoryId, pushed, cancellationToken);
                        delivery.Handled(clock.Now);
                        break;

                    case GitEvent.PullRequestChanged changed:
                        await RecordAsync(repositoryId, changed.Change, cancellationToken);
                        delivery.Handled(clock.Now);
                        break;

                    case GitEvent.Reviewed reviewed:
                        await RecordAsync(repositoryId, reviewed, cancellationToken);
                        delivery.Handled(clock.Now);
                        break;

                    case GitEvent.Uninteresting uninteresting:
                        delivery.Ignored(uninteresting.Why, clock.Now);
                        break;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            /*
             * Caught per delivery rather than per batch, so that one payload
             * whose shape nobody anticipated does not stop the fifty behind it
             * from being handled. The alternative — a batch that aborts on the
             * first failure — means one unparseable delivery silently halts the
             * whole integration, and it stays halted, because the same delivery
             * is at the front of the queue on the next pass too.
             */
            logger.LogError(
                exception,
                "Delivery {Delivery} ({Event} from {Provider}) could not be handled on attempt "
                + "{Attempt}.",
                delivery.Id,
                delivery.Event,
                delivery.Provider,
                delivery.Attempts + 1);

            delivery.Failed(Short(exception), clock.Now);

            if (delivery.Status == DeliveryStatus.DeadLettered)
            {
                Telemetry.DeliveriesDeadLettered.Add(
                    1, new KeyValuePair<string, object?>("event", delivery.Event));
            }
        }

        /*
         * Saved inside the loop, after each delivery. A single save at the end
         * would mean a crash halfway through a batch loses the record of
         * everything that had already been handled, and every one of those
         * deliveries would be handled a second time on the next pass.
         */
        await repositories.SaveAsync(cancellationToken);
    }

    /// <summary>Record the commits a push carried.</summary>
    /// <remarks>
    /// Each commit is checked against the ones already stored. A push carries
    /// everything new to the remote, and the same commit arrives again on a
    /// merge, a force-push, or a push of the same branch somewhere else — so the
    /// check is not defensive, it is the ordinary case.
    /// </remarks>
    private async Task RecordAsync(
        Guid repositoryId, GitEvent.Pushed pushed, CancellationToken cancellationToken)
    {
        foreach (var commit in pushed.Commits)
        {
            if (await repositories.CommitKnownAsync(commit.Sha, cancellationToken))
            {
                continue;
            }

            var workItemId = await WorkItemFor(
                cancellationToken, pushed.Branch, commit.Message);

            repositories.Add(Commit.Record(
                repositoryId,
                commit.Sha,
                commit.Message,
                commit.Author,
                pushed.Branch,
                workItemId,
                commit.At));
        }
    }

    private async Task RecordAsync(
        Guid repositoryId, PullRequestChange change, CancellationToken cancellationToken)
    {
        var existing = await repositories.PullRequestAsync(
            repositoryId, change.Number, cancellationToken);

        if (existing is null)
        {
            /*
             * Created whatever the action was, including a merge or a close.
             * A pull request this system never saw opened is normal — the
             * webhook was added after the branch was pushed, or the delivery
             * that opened it is still dead-lettered — and refusing to record
             * the merge would lose the single most useful event in the set.
             */
            var workItemId = await WorkItemFor(
                cancellationToken, change.Branch, change.Title);

            var opened = PullRequest.Opened(
                repositoryId,
                change.Number,
                change.Title,
                change.Branch,
                change.Author,
                workItemId,
                change.At);

            Apply(opened, change);
            repositories.Add(opened);
            return;
        }

        if (change.Title != existing.Title)
        {
            existing.Retitle(change.Title);
        }

        /*
         * A retitle can add the reference that was missing when it was opened,
         * which is exactly what happens when somebody is told their branch name
         * was wrong and fixes the title instead of the branch. Only ever fills
         * a gap: a link already made stands, because a person may have set it
         * deliberately on the work item page and a title should not overrule
         * them.
         */
        if (existing.WorkItemId is null
            && await WorkItemFor(cancellationToken, change.Branch, change.Title) is { } found)
        {
            existing.Belongs(found);
        }

        Apply(existing, change);
    }

    private static void Apply(PullRequest pullRequest, PullRequestChange change)
    {
        switch (change.Action)
        {
            case PullRequestAction.Merged:
                pullRequest.Merged(change.At);
                break;

            case PullRequestAction.Closed:
                pullRequest.Closed(change.At);
                break;
        }
    }

    private async Task RecordAsync(
        Guid repositoryId, GitEvent.Reviewed reviewed, CancellationToken cancellationToken)
    {
        var pullRequest = await repositories.PullRequestAsync(
            repositoryId, reviewed.Number, cancellationToken);

        if (pullRequest is null)
        {
            /*
             * Thrown rather than ignored, so the delivery is retried. A review
             * arriving before the pull request it is on means the two deliveries
             * are being handled out of order, which a later pass fixes on its
             * own — and swallowing it would silently drop the approval that a
             * release gate is waiting for.
             */
            throw new InvalidOperationException(
                $"Pull request #{reviewed.Number} is not recorded yet, so the review on it "
                + "cannot be attached. This delivery will be tried again.");
        }

        pullRequest.Reviewed(
            reviewed.ExternalId, reviewed.Reviewer, reviewed.Verdict, clock.Now);
    }

    /// <summary>
    /// The work item somebody named, if they named one that exists.
    /// </summary>
    /// <remarks>
    /// Two failures are possible and they are not the same. Nobody wrote a
    /// reference, which is common and fine — the commit is recorded unattached
    /// and somebody can attach it. Or somebody wrote a reference to a number
    /// that is not a work item, which is a typo worth a log line, because the
    /// developer believes they have linked their work and they have not.
    /// </remarks>
    private async Task<Guid?> WorkItemFor(
        CancellationToken cancellationToken, params string?[] text)
    {
        if (WorkReference.FirstIn(text) is not { } number)
        {
            return null;
        }

        var item = await work.ByNumberAsync(number, cancellationToken);

        if (item is null)
        {
            logger.LogInformation(
                "A commit or pull request referred to work item #{Number}, which does not "
                + "exist. It has been recorded without a link.",
                number);
        }

        return item?.Id;
    }

    /// <summary>
    /// The message, without the stack.
    /// </summary>
    /// <remarks>
    /// The full exception is already in the log with its stack trace. What is
    /// stored on the delivery is what a person reads on a screen while deciding
    /// whether to replay it, and a column holding forty frames of stack is one
    /// nobody reads at all.
    /// </remarks>
    private static string Short(Exception exception)
    {
        var message = exception.Message;

        return message.Length > 400 ? message[..400] : message;
    }
}
