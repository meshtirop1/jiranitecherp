using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Engineering;

/// <summary>Where a delivery got to.</summary>
public enum DeliveryStatus
{
    /// <summary>Written down, not yet acted on.</summary>
    Received = 1,

    /// <summary>Acted on. Whatever it described has been recorded.</summary>
    Handled = 2,

    /// <summary>Understood and deliberately not acted on.</summary>
    /// <remarks>
    /// A real outcome rather than a failure. Providers send far more than this
    /// system cares about — stars, forks, wiki edits, label changes — and a
    /// delivery that was read and found uninteresting must be distinguishable
    /// from one that broke, or the failure list fills with noise and nobody
    /// reads it.
    /// </remarks>
    Ignored = 3,

    /// <summary>Tried and failed. Will be tried again.</summary>
    Failed = 4,

    /// <summary>Tried enough times. Waiting for a person.</summary>
    DeadLettered = 5,
}

/// <summary>
/// One delivery from a provider, exactly as it arrived.
/// </summary>
/// <remarks>
/// This is the integration's memory, and everything trustworthy about it comes
/// from the fact that the delivery is written down <em>before</em> it is
/// understood. A handler that throws, a payload shape that changed, a provider
/// that retried four times — none of those lose anything, because the raw body
/// is already on disk and can be run through corrected code afterwards.
///
/// The alternative, parsing at the door and storing only the result, fails in a
/// particular way: the deliveries lost are exactly the ones whose shape was
/// guessed wrong, so the bug hides its own evidence.
///
/// Four separate concerns are settled here, and they are easy to confuse:
///
///   <b>Authenticity</b> — the signature proves the body came from somebody
///   holding the secret. Checked before anything is written, because an
///   unsigned body is not a delivery, it is a stranger posting to an open URL.
///
///   <b>Idempotency</b> — providers retry, and a retry is not a second event.
///   <see cref="ExternalId"/> is unique per provider, so the second copy of a
///   push is refused by the database rather than by a handler that somebody
///   remembered to make idempotent.
///
///   <b>Replay</b> — the same uniqueness stops a captured body being posted
///   back later. This is worth being explicit about: GitHub sends no timestamp
///   to check freshness against, so the delivery identifier is the <em>only</em>
///   replay protection there is, and it works for exactly as long as these rows
///   are kept. Deleting old deliveries to save space reopens the window.
///
///   <b>Failure</b> — a delivery that cannot be handled is retried, and after
///   enough attempts it stops being retried and starts being somebody's
///   problem. A queue that retries forever is a queue nobody looks at.
/// </remarks>
public sealed class WebhookDelivery : Entity, IAuditable
{
    /// <summary>
    /// How many times a delivery is tried before it becomes a person's problem.
    /// </summary>
    /// <remarks>
    /// Five, because the failures worth retrying are transient — a database
    /// blocked, a deadlock, a moment of network — and five spread over backoff
    /// outlasts all of them. Anything still failing after five is failing for a
    /// reason that will not pass on its own, and continuing to retry it only
    /// hides that from whoever could fix it.
    /// </remarks>
    public const int MaximumAttempts = 5;

    private WebhookDelivery()
    {
        ExternalId = string.Empty;
        Event = string.Empty;
        Payload = string.Empty;
    }

    private WebhookDelivery(
        GitProvider provider,
        string externalId,
        string eventName,
        string payload,
        Guid? repositoryId,
        DateTimeOffset at)
    {
        Provider = provider;
        ExternalId = Required(externalId, nameof(externalId));
        Event = Required(eventName, nameof(eventName));
        Payload = payload;
        RepositoryId = repositoryId;
        Status = DeliveryStatus.Received;
        ReceivedAt = at;

        Raise(new WebhookDeliveryReceived(Id, provider, Event, repositoryId, at));
    }

    /// <summary>Write a delivery down, before trying to understand it.</summary>
    public static WebhookDelivery Receive(
        GitProvider provider,
        string externalId,
        string eventName,
        string payload,
        Guid? repositoryId,
        DateTimeOffset at) =>
        new(provider, externalId, eventName, payload, repositoryId, at);

    public GitProvider Provider { get; private init; }

    /// <summary>
    /// The provider's own identifier for this delivery.
    /// </summary>
    /// <remarks>
    /// The X-GitHub-Delivery header and its equivalents. Unique together with
    /// the provider, and that index is what makes the whole thing safe to
    /// retry: the second arrival is refused by Postgres rather than by a
    /// handler that somebody remembered to write carefully.
    /// </remarks>
    public string ExternalId { get; private init; }

    /// <summary>What the provider called it: push, pull_request.</summary>
    public string Event { get; private init; }

    /// <summary>
    /// The body, verbatim.
    /// </summary>
    /// <remarks>
    /// Kept whole and unparsed. It is what a dead-lettered delivery is replayed
    /// from once the reason it failed has been fixed, and without it a bad week
    /// of deliveries is simply gone.
    /// </remarks>
    public string Payload { get; private init; }

    /// <summary>The repository it was matched to, if it matched one.</summary>
    /// <remarks>
    /// Nullable on purpose. A delivery naming a repository nobody connected is
    /// still recorded — it is the evidence that somebody pointed a webhook here
    /// and nothing is listening, which is otherwise a silence indistinguishable
    /// from having no webhook at all.
    /// </remarks>
    public Guid? RepositoryId { get; private init; }

    public DeliveryStatus Status { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset ReceivedAt { get; private init; }

    public DateTimeOffset? HandledAt { get; private set; }

    /// <summary>Why it failed, for whoever has to fix it.</summary>
    public string? Error { get; private set; }

    public bool IsWaiting => Status is DeliveryStatus.Received or DeliveryStatus.Failed;

    public bool NeedsSomebody => Status == DeliveryStatus.DeadLettered;

    /// <summary>It was acted on.</summary>
    public void Handled(DateTimeOffset at)
    {
        Status = DeliveryStatus.Handled;
        HandledAt = at;
        Error = null;
    }

    /// <summary>It was read, and there was nothing here for us.</summary>
    public void Ignored(string why, DateTimeOffset at)
    {
        Status = DeliveryStatus.Ignored;
        HandledAt = at;
        Error = why;
    }

    /// <summary>
    /// It failed, and this is the attempt that failed.
    /// </summary>
    /// <remarks>
    /// The attempt count moves here rather than where the retry is scheduled,
    /// so that a process killed between failing and rescheduling still counts
    /// the attempt. A counter that only advances on the happy path is a counter
    /// that lets a poisonous delivery retry forever.
    /// </remarks>
    public void Failed(string error, DateTimeOffset at)
    {
        Attempts++;
        Error = error;

        if (Attempts >= MaximumAttempts)
        {
            Status = DeliveryStatus.DeadLettered;
            Raise(new WebhookDeliveryDeadLettered(Id, Provider, Event, error, at));
            return;
        }

        Status = DeliveryStatus.Failed;
    }

    /// <summary>
    /// Put a dead-lettered delivery back in the queue.
    /// </summary>
    /// <remarks>
    /// The point of keeping the body. Somebody fixes the handler, replays the
    /// deliveries that broke on it, and the history fills itself in rather than
    /// staying permanently wrong.
    ///
    /// The attempt count resets, because the attempts that failed were made
    /// against code that no longer exists.
    /// </remarks>
    public void Replay()
    {
        if (Status != DeliveryStatus.DeadLettered)
        {
            return;
        }

        Status = DeliveryStatus.Received;
        Attempts = 0;
        Error = null;
    }

    /// <summary>
    /// The body is excluded from the trail.
    /// </summary>
    /// <remarks>
    /// It is already stored here in full, and a payload can run to tens of
    /// kilobytes. Copying every one of them into the audit trail as an old
    /// value and a new value would double the largest table in the database to
    /// record that an immutable column did not change.
    /// </remarks>
    public static IReadOnlySet<string> AuditExcludes { get; } =
        new HashSet<string> { nameof(Payload) };

    private static string Required(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();
}

public sealed record WebhookDeliveryReceived(
    Guid DeliveryId,
    GitProvider Provider,
    string Event,
    Guid? RepositoryId,
    DateTimeOffset At) : DomainEvent;

/// <summary>
/// A delivery gave up.
/// </summary>
/// <remarks>
/// An event rather than a log line, because this is the moment the integration
/// stopped being trustworthy and somebody has to be told. Silent dead-lettering
/// is how a board slowly stops matching the repository while everybody carries
/// on believing it.
/// </remarks>
public sealed record WebhookDeliveryDeadLettered(
    Guid DeliveryId,
    GitProvider Provider,
    string Event,
    string Error,
    DateTimeOffset At) : DomainEvent;
