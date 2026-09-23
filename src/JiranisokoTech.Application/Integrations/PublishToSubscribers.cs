using System.Text.Json;
using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Domain.Integrations;
using Microsoft.Extensions.Logging;

namespace JiranisokoTech.Application.Integrations;

/// <summary>
/// Turns one of this system's events into a notification for everybody who asked.
/// </summary>
/// <remarks>
/// Generic and registered once per event in <see cref="OutboundEvents.Offered"/>, so
/// the catalogue of what may leave the building is a list in one file and a matching
/// set of registrations, rather than fourteen near-identical classes that would
/// drift.
///
/// It only queues. Nothing is sent from here, and that is deliberate: this runs from
/// the outbox, and the outbox exists to run the firm's own handlers promptly. A
/// handler that waited on somebody else's unreachable server would hold up the
/// emails behind it.
/// </remarks>
public sealed class PublishToSubscribers<TEvent>(
    IIntegrationRepository integrations,
    IClock clock,
    ILogger<PublishToSubscribers<TEvent>> logger)
    : IDomainEventHandler<TEvent>
    where TEvent : IDomainEvent
{
    /// <summary>
    /// How the body is written.
    /// </summary>
    /// <remarks>
    /// Camel case, because this is a public contract read by somebody else's code and
    /// every webhook they have ever consumed was camel case. Nulls are kept rather
    /// than dropped: a receiver distinguishing "no work item" from "the field is gone"
    /// needs the field to be there.
    /// </remarks>
    private static readonly JsonSerializerOptions Format = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task HandleAsync(
        TEvent domainEvent, CancellationToken cancellationToken = default)
    {
        var name = typeof(TEvent).Name;
        var wanting = await integrations.WantingAsync(name, cancellationToken);

        if (wanting.Count == 0)
        {
            return;
        }

        /*
         * Serialised once for all of them, so every subscriber receives the same
         * bytes. Not an optimisation: the body is what gets signed, and two
         * subscribers holding different bodies for one event would make any
         * comparison between their records meaningless.
         */
        var body = JsonSerializer.Serialize(
            new Notification(name, clock.Now, domainEvent), Format);

        foreach (var subscription in wanting)
        {
            integrations.Add(OutboundDelivery.Queue(subscription.Id, name, body, clock.Now));
        }

        await integrations.SaveAsync(cancellationToken);

        logger.LogInformation(
            "{Event} queued for {Count} subscriber(s).", name, wanting.Count);
    }

    /// <summary>
    /// The envelope every notification arrives in.
    /// </summary>
    /// <remarks>
    /// An envelope rather than the bare event, because a receiver needs to know what
    /// it is looking at before it parses the inside, and needs a timestamp to order
    /// notifications that arrive out of order — which they will, since each is
    /// retried independently.
    /// </remarks>
    private sealed record Notification(string Event, DateTimeOffset At, TEvent Data);
}
