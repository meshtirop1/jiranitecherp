using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Integrations;

public enum OutboundStatus
{
    /// <summary>Queued, not sent yet.</summary>
    Waiting = 1,

    /// <summary>The far end accepted it.</summary>
    Sent = 2,

    /// <summary>The far end did not, and it will be tried again.</summary>
    Failed = 3,

    /// <summary>Tried enough times. Waiting for a person.</summary>
    DeadLettered = 4,
}

/// <summary>
/// One attempt to tell somebody outside that something happened.
/// </summary>
/// <remarks>
/// Deliberately a mirror of <see cref="Engineering.WebhookDelivery"/> pointing the
/// other way, and for the same reason: the thing to be sent is written down before
/// anybody tries to send it. An endpoint that is down, a DNS failure, a deploy at
/// the far end — none of them lose the notification, because it is already a row.
///
/// A queue of its own rather than the outbox, and that division is worth naming.
/// The outbox exists to run this system's own handlers exactly once after a
/// transaction commits; those handlers are ours, they are fast, and they either work
/// or are broken. This queue talks to somebody else's server over the internet, on
/// their availability, with their timeouts. Putting the two in one queue would mean
/// a customer's unreachable endpoint delaying the firm's own emails, and an outbox
/// backlog that is really just one third party being offline.
/// </remarks>
public sealed class OutboundDelivery : Entity, IAuditable
{
    /// <summary>
    /// How many attempts before a notification becomes a person's problem.
    /// </summary>
    /// <remarks>
    /// Six, spread over the backoff below, spans roughly three hours — long enough
    /// to ride out a deploy or a restart at the far end, short enough that a genuinely
    /// dead endpoint is disabled the same day rather than next week.
    /// </remarks>
    public const int MaximumAttempts = 6;

    private OutboundDelivery()
    {
        Event = string.Empty;
        Payload = string.Empty;
    }

    private OutboundDelivery(
        Guid subscriptionId, string eventName, string payload, DateTimeOffset at)
    {
        SubscriptionId = subscriptionId;
        Event = Required(eventName, nameof(eventName));
        Payload = payload;
        Status = OutboundStatus.Waiting;
        QueuedAt = at;
        NextAttemptAt = at;
    }

    public static OutboundDelivery Queue(
        Guid subscriptionId, string eventName, string payload, DateTimeOffset at) =>
        new(subscriptionId, eventName, payload, at);

    public Guid SubscriptionId { get; private init; }

    public string Event { get; private init; }

    /// <summary>The body, as it will be signed and sent.</summary>
    /// <remarks>
    /// Stored rather than rebuilt from the event on each attempt. A retry has to send
    /// byte-for-byte what the first attempt sent, or the signature the receiver
    /// checks is over something else — and a receiver deduplicating on the body would
    /// see two different notifications for one thing.
    /// </remarks>
    public string Payload { get; private init; }

    public OutboundStatus Status { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset QueuedAt { get; private init; }

    /// <summary>When it may next be tried, which is what spreads the retries out.</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    /// <summary>The status the far end answered with, if it answered at all.</summary>
    public int? ResponseCode { get; private set; }

    public string? Error { get; private set; }

    public bool IsWaiting => Status is OutboundStatus.Waiting or OutboundStatus.Failed;

    public void Sent(int responseCode, DateTimeOffset at)
    {
        Attempts++;
        Status = OutboundStatus.Sent;
        ResponseCode = responseCode;
        SentAt = at;
        NextAttemptAt = null;
        Error = null;
    }

    /// <summary>
    /// It failed, and this is when to try again.
    /// </summary>
    /// <remarks>
    /// The caller supplies the delay so the backoff policy lives in one place with
    /// the rest of the settings rather than being decided by an entity that has no
    /// business knowing how long a minute is.
    /// </remarks>
    public void Failed(string error, int? responseCode, TimeSpan retryAfter, DateTimeOffset at)
    {
        Attempts++;
        Error = Trim(error);
        ResponseCode = responseCode;

        if (Attempts >= MaximumAttempts)
        {
            Status = OutboundStatus.DeadLettered;
            NextAttemptAt = null;

            Raise(new OutboundDeliveryDeadLettered(
                Id, SubscriptionId, Event, Error ?? "Unknown.", at));

            return;
        }

        Status = OutboundStatus.Failed;
        NextAttemptAt = at + retryAfter;
    }

    /// <summary>
    /// Stop trying, without spending the attempts.
    /// </summary>
    /// <remarks>
    /// For when retrying is known to be pointless rather than merely unsuccessful —
    /// the subscription was switched off, or its secret can no longer be read. Those
    /// are not transient, and letting the delivery burn six attempts against a
    /// certainty would only delay the row saying so by three hours.
    /// </remarks>
    public void Abandon(string why, DateTimeOffset at)
    {
        if (Status is OutboundStatus.Sent or OutboundStatus.DeadLettered)
        {
            return;
        }

        Status = OutboundStatus.DeadLettered;
        Error = Trim(why);
        NextAttemptAt = null;

        Raise(new OutboundDeliveryDeadLettered(Id, SubscriptionId, Event, Error ?? why, at));
    }

    /// <summary>Put a dead-lettered notification back in the queue.</summary>
    public void Retry(DateTimeOffset at)
    {
        if (Status != OutboundStatus.DeadLettered)
        {
            return;
        }

        Status = OutboundStatus.Waiting;
        Attempts = 0;
        NextAttemptAt = at;
        Error = null;
    }

    /// <summary>
    /// The body is excluded from the trail.
    /// </summary>
    /// <remarks>
    /// It is stored here in full and it describes an invoice or a person. Copying
    /// every one of them into the audit trail would put a second copy of the firm's
    /// data in a table with different retention, to record that an immutable column
    /// did not change.
    /// </remarks>
    public static IReadOnlySet<string> AuditExcludes { get; } =
        new HashSet<string> { nameof(Payload) };

    private static string Required(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();

    private static string Trim(string error) =>
        error.Length > 400 ? error[..400] : error;
}

/// <summary>
/// A notification gave up.
/// </summary>
/// <remarks>
/// An event rather than a log line, because somebody outside this firm is now
/// missing something they were promised, and the firm does not know it unless this
/// is said out loud.
/// </remarks>
public sealed record OutboundDeliveryDeadLettered(
    Guid DeliveryId,
    Guid SubscriptionId,
    string Event,
    string Error,
    DateTimeOffset At) : DomainEvent;
