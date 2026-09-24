namespace JiranisokoTech.Infrastructure.Persistence;

/// <summary>
/// A domain event, written in the same transaction as the change that caused it.
/// </summary>
/// <remarks>
/// This is the transactional outbox, and it exists to close a gap that is easy
/// to miss and very hard to debug.
///
/// The obvious approach — save the change, then publish the event — has a window
/// between the two. A process that dies in that window has committed a hire with
/// nobody told, an invoice paid with no receipt sent, a deployment recorded and
/// no notification. Nothing in the system knows it happened, because the only
/// record of the intent was in memory.
///
/// Writing the event as a row in the same transaction removes the window
/// entirely: either both land or neither does. The dispatcher then reads unsent
/// rows and publishes them, and because it can crash and retry, handlers must be
/// idempotent — which is a requirement worth designing for rather than
/// discovering.
///
/// A row is in exactly one of four states, and the columns say which:
///
///   pending     — DispatchedAt and AbandonedAt both null. Waiting, or waiting
///                 out a backoff after a failure.
///   claimed     — ClaimedBy set. A dispatcher has taken it. A claim goes stale
///                 on a timeout, so a process that dies holding one does not
///                 strand the message forever.
///   dispatched  — DispatchedAt set. Every handler ran without throwing.
///   abandoned   — AbandonedAt set. Gave up. Nothing retries it, and it stays in
///                 the table with its last error so somebody can see it.
/// </remarks>
public sealed class OutboxMessage
{
    private OutboxMessage()
    {
        Type = string.Empty;
        Payload = string.Empty;
    }

    public OutboxMessage(string type, string payload, DateTimeOffset occurredAt)
    {
        Id = Guid.CreateVersion7();
        Type = type;
        Payload = payload;
        OccurredAt = occurredAt;
    }

    public Guid Id { get; private init; }

    /// <summary>
    /// The event's type name, used to rebuild it.
    /// </summary>
    /// <remarks>
    /// Stored as a plain name rather than an assembly-qualified one. A row
    /// written before a refactor must still be readable after it, and
    /// assembly-qualified names turn "we moved a namespace" into "the queue
    /// cannot be drained".
    /// </remarks>
    public string Type { get; private init; }

    /// <summary>The event as JSON. Ids and values, never entities.</summary>
    public string Payload { get; private init; }

    public DateTimeOffset OccurredAt { get; private init; }

    public DateTimeOffset? DispatchedAt { get; private set; }

    /// <summary>How many times dispatch has been attempted and failed.</summary>
    public int Attempts { get; private set; }

    /// <summary>When the last attempt was made. For whoever is reading the row.</summary>
    public DateTimeOffset? LastAttemptedAt { get; private set; }

    /// <summary>
    /// The earliest this may be tried again. Null means now.
    /// </summary>
    /// <remarks>
    /// Stored rather than worked out at read time, deliberately. The backoff
    /// depends on how many attempts a row has behind it, and a rule like
    /// "last attempt plus a delay that varies per row" cannot be put in a WHERE
    /// clause that an index can use. Computing it once on failure turns the
    /// dispatcher's query back into a range scan.
    ///
    /// Without a backoff at all, a failing message is retried on every poll —
    /// several times a minute, forever, each one writing a line to the log,
    /// which then becomes useless at exactly the moment somebody needs to read
    /// it.
    /// </remarks>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    /// <summary>The last failure, kept so a stuck message can be diagnosed.</summary>
    public string? Error { get; private set; }

    /// <summary>
    /// Which dispatcher run has taken this message, if any.
    /// </summary>
    /// <remarks>
    /// Two instances of the application both poll this table. Without a claim
    /// they would both read the same pending row and both publish it — two
    /// rejection emails to the same candidate, from a system that looks correct
    /// in every log. The claim is taken with a conditional update, so exactly
    /// one of them wins.
    /// </remarks>
    public Guid? ClaimedBy { get; private set; }

    public DateTimeOffset? ClaimedAt { get; private set; }

    /// <summary>When this was given up on. Nothing retries it afterwards.</summary>
    public DateTimeOffset? AbandonedAt { get; private set; }

    public bool IsPending => DispatchedAt is null && AbandonedAt is null;

    public void MarkDispatched(DateTimeOffset at)
    {
        DispatchedAt = at;
        LastAttemptedAt = at;
        NextAttemptAt = null;
        Error = null;
        Release();
    }

    public void MarkFailed(string error, DateTimeOffset at, TimeSpan retryIn)
    {
        Attempts++;
        LastAttemptedAt = at;
        NextAttemptAt = at + retryIn;
        Error = Shorten(error);

        Release();
    }

    /// <summary>
    /// Stop trying.
    /// </summary>
    /// <remarks>
    /// For the two cases where retrying cannot help: an event type the code no
    /// longer has, and a handler that has thrown the same way too many times.
    /// The row stays, with its error, because a message quietly deleted is a
    /// thing that happened and cannot be explained afterwards.
    /// </remarks>
    public void Abandon(string reason, DateTimeOffset at)
    {
        AbandonedAt = at;
        LastAttemptedAt = at;
        NextAttemptAt = null;
        Error = Shorten(reason);

        Release();
    }

    /// <summary>
    /// Put an abandoned message back in the queue.
    /// </summary>
    /// <remarks>
    /// The point of keeping abandoned rows rather than deleting them. A handler
    /// throws, eight attempts burn through in four hours, and the event is lost —
    /// a leaver's work never released, a letter never sent. Somebody fixes the
    /// handler, deploys, and without this there is no way to make the events that
    /// broke on the old code happen. The queue depth on the machinery screen says
    /// something needs a person and offers nothing a person can do.
    ///
    /// The attempt count resets, matching <c>WebhookDelivery.Replay</c> and
    /// <c>OutboundDelivery.Retry</c>, and for the same reason: the attempts that
    /// failed were made against code that no longer exists, so charging them
    /// against the fix would abandon it again on the first hiccup.
    ///
    /// <b>Reviving does not promise the message will go through.</b> A message
    /// abandoned because its event type is no longer in the code will be abandoned
    /// again on the next pass, within seconds, with the same reason. That is the
    /// honest outcome and it is visible — the alternative, refusing to revive
    /// anything the dispatcher might refuse, would mean this class second-guessing
    /// the registry from the wrong side of the process.
    /// </remarks>
    public void Revive(DateTimeOffset at)
    {
        if (AbandonedAt is null)
        {
            return;
        }

        AbandonedAt = null;
        Attempts = 0;
        NextAttemptAt = at;
        Error = null;

        Release();
    }

    private void Release()
    {
        ClaimedBy = null;
        ClaimedAt = null;
    }

    /// <summary>
    /// Truncated: a stack trace from a handler can run to kilobytes, and a
    /// hundred of them turn the outbox into the largest table in the database.
    /// The full detail belongs in the log.
    /// </summary>
    private static string Shorten(string message) =>
        message.Length > 2000 ? message[..2000] : message;
}
