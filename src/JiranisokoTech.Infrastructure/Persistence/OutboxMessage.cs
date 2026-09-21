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
/// entirely: either both land or neither does. A dispatcher then reads unsent
/// rows and publishes them, and because it can crash and retry, handlers must be
/// idempotent — which is a requirement worth designing for rather than
/// discovering.
///
/// The dispatcher is not built yet. Rows accumulate here and nothing reads them,
/// which is deliberate: the table is the part that must exist first, because an
/// event not written at the moment it happened cannot be recovered later.
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

    /// <summary>The last failure, kept so a stuck message can be diagnosed.</summary>
    public string? Error { get; private set; }

    public bool IsPending => DispatchedAt is null;

    public void MarkDispatched(DateTimeOffset at)
    {
        DispatchedAt = at;
        Error = null;
    }

    public void MarkFailed(string error)
    {
        Attempts++;

        // Truncated: a stack trace from a handler can run to kilobytes, and a
        // hundred of them turn the outbox into the largest table in the
        // database. The full detail belongs in the log.
        Error = error.Length > 2000 ? error[..2000] : error;
    }
}
