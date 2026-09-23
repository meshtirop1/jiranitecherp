using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Observability;
using JiranisokoTech.Domain.Integrations;
using Microsoft.Extensions.Logging;

namespace JiranisokoTech.Application.Integrations;

/// <summary>What happened when a notification was posted.</summary>
/// <remarks>
/// A result rather than an exception for the ordinary failures, because an endpoint
/// answering 500 or timing out is the expected weather of talking to somebody else's
/// server and not an exceptional condition in this process.
/// </remarks>
public sealed record SendResult(bool Accepted, int? ResponseCode, string? Error)
{
    public static SendResult Ok(int responseCode) => new(true, responseCode, null);

    public static SendResult Refused(int responseCode, string error) =>
        new(false, responseCode, error);

    public static SendResult Unreachable(string error) => new(false, null, error);
}

/// <summary>
/// Posts a signed notification to one endpoint.
/// </summary>
/// <remarks>
/// An interface so the dispatcher's rules — batching, backoff, giving up, disabling a
/// subscription — can be tested without a network, and so the one place that talks to
/// the outside world is nameable.
/// </remarks>
public interface IOutboundSender
{
    Task<SendResult> SendAsync(
        Subscription subscription,
        OutboundDelivery delivery,
        string secret,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Sends the notifications that are due, and gives up on the ones that will not go.
/// </summary>
/// <remarks>
/// The outgoing twin of <see cref="Engineering.DeliveryDispatcher"/>. Only the work is
/// here; the loop that calls it lives in the infrastructure, so a test can run exactly
/// one pass and assert on what happened.
/// </remarks>
public sealed class OutboundDispatcher(
    IIntegrationRepository integrations,
    IOutboundSender sender,
    ISecretStore secrets,
    IClock clock,
    ILogger<OutboundDispatcher> logger)
{
    /// <summary>The first wait after a failure. It doubles with each attempt.</summary>
    public static TimeSpan FirstRetryDelay { get; } = TimeSpan.FromSeconds(30);

    /// <summary>The longest a retry ever waits, so doubling does not run away.</summary>
    public static TimeSpan MaximumRetryDelay { get; } = TimeSpan.FromHours(1);

    /// <summary>Send one batch. Returns how many were attempted.</summary>
    public async Task<int> RunOnceAsync(
        int batchSize = 25, CancellationToken cancellationToken = default)
    {
        var due = await integrations.DueAsync(clock.Now, batchSize, cancellationToken);

        foreach (var delivery in due)
        {
            await SendAsync(delivery, cancellationToken);
        }

        return due.Count;
    }

    private async Task SendAsync(
        OutboundDelivery delivery, CancellationToken cancellationToken)
    {
        var subscription = await integrations.FindAsync(delivery.SubscriptionId, cancellationToken);

        if (subscription is null || !subscription.IsActive)
        {
            /*
             * Dropped rather than kept waiting. A subscription switched off — by a
             * person or by itself — is a statement that this endpoint should stop
             * being called, and a queue that kept its backlog would empty it all at
             * whoever owns that address the moment somebody switched it back on.
             */
            delivery.Abandon(
                subscription is null
                    ? "The subscription no longer exists."
                    : "The subscription was switched off before this could be sent.",
                clock.Now);

            await integrations.SaveAsync(cancellationToken);
            return;
        }

        if (secrets.Reveal(subscription.ProtectedSecret) is not { Length: > 0 } secret)
        {
            /*
             * The key ring cannot read this back, which in practice means it was
             * replaced or lost. Retrying will not help and neither will waiting, so
             * the subscription is switched off with a reason somebody can act on —
             * re-entering the secret is the fix.
             */
            logger.LogError(
                "The signing secret for subscription {Subscription} could not be read back, so "
                + "nothing can be sent to it.",
                subscription.Name);

            subscription.Disable(
                "The signing secret could not be read back — most likely the key ring changed. "
                + "Remove this subscription and add it again with the secret.",
                clock.Now);

            delivery.Abandon("The signing secret could not be read back.", clock.Now);

            await integrations.SaveAsync(cancellationToken);
            return;
        }

        SendResult result;

        try
        {
            result = await sender.SendAsync(subscription, delivery, secret, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Caught per delivery, so one endpoint behaving strangely does not stop
            // the notifications queued behind it from going out.
            result = SendResult.Unreachable(exception.Message);
        }

        if (result.Accepted)
        {
            delivery.Sent(result.ResponseCode ?? 200, clock.Now);
            subscription.Delivered(clock.Now);

            Telemetry.NotificationsSent.Add(
                1, new KeyValuePair<string, object?>("event", delivery.Event));
        }
        else
        {
            delivery.Failed(
                result.Error ?? "The endpoint did not accept it.",
                result.ResponseCode,
                RetryDelayAfter(delivery.Attempts + 1),
                clock.Now);

            if (delivery.Status == OutboundStatus.DeadLettered)
            {
                // Only a delivery that has run out of attempts counts against the
                // subscription. Counting every failed attempt would switch off an
                // endpoint after two bad minutes.
                subscription.Failed(clock.Now);

                Telemetry.NotificationsFailed.Add(
                    1, new KeyValuePair<string, object?>("event", delivery.Event));

                logger.LogWarning(
                    "A {Event} notification to {Subscription} gave up after {Attempts} "
                    + "attempts: {Error}",
                    delivery.Event,
                    subscription.Name,
                    delivery.Attempts,
                    delivery.Error);
            }
        }

        // Saved per delivery. A crash halfway through a batch would otherwise lose
        // the record of everything already sent, and every one of those endpoints
        // would be told the same thing twice on the next pass.
        await integrations.SaveAsync(cancellationToken);
    }

    /// <summary>How long to wait after a failure with this many attempts behind it.</summary>
    /// <remarks>
    /// Doubling, in ticks, with the shift done on a long so that a large attempt count
    /// cannot overflow into a negative delay and produce a notification that retries
    /// instantly forever. The same reasoning as the outbox's own backoff.
    /// </remarks>
    public static TimeSpan RetryDelayAfter(int attempts)
    {
        if (attempts <= 1)
        {
            return FirstRetryDelay;
        }

        var shift = Math.Min(attempts - 1, 32);
        var ticks = FirstRetryDelay.Ticks * (1L << shift);

        return ticks <= 0 || ticks > MaximumRetryDelay.Ticks
            ? MaximumRetryDelay
            : TimeSpan.FromTicks(ticks);
    }
}
