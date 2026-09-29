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
    /// <summary>Which kind of destination this one knows how to talk to.</summary>
    /// <remarks>
    /// Declared by the sender rather than mapped in a table somewhere above it, which is the
    /// whole of section 51's "a provider can be added without changing core business logic": a new
    /// provider is a new file with a new value here and a registration, and the dispatcher's rules
    /// are untouched. A table would be a second place that has to learn about every provider, and
    /// the day somebody forgot to edit it the subscription would be sent in the wrong shape.
    /// </remarks>
    DestinationKind Handles { get; }

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
    IEnumerable<IOutboundSender> senders,
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
        using var span = Telemetry.Source.StartActivity("outbound webhook");
        span?.SetTag("event", delivery.Event);

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

        /*
         * Chosen by the subscription's kind, and a missing sender is treated as an endpoint that
         * refused rather than as an exception.
         *
         * It is a wiring fault and not a data fault — a value on the enum that nothing was
         * registered for — so it cannot be fixed by the person reading the screen. But throwing
         * here would take out the whole batch, including the deliveries queued behind it for
         * destinations that work. Refusing lets the ordinary machinery do the right thing: this
         * one retries, gives up, and after ten give-ups the subscription switches itself off with
         * a reason on the screen, while everything else keeps going.
         */
        if (senders.FirstOrDefault(one => one.Handles == subscription.Kind) is not { } sender)
        {
            logger.LogError(
                "Nothing is registered to send to a {Kind} destination, so {Subscription} cannot "
                + "be told anything.",
                subscription.Kind,
                subscription.Name);

            delivery.Failed(
                $"Nothing in this application knows how to post to a {subscription.Kind} "
                + "destination.",
                null,
                RetryDelayAfter(delivery.Attempts + 1),
                clock.Now);

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
