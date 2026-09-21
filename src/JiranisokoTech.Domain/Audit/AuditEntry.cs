namespace JiranisokoTech.Domain.Audit;

/// <summary>
/// One recorded act: who did what to which record, when, and why.
/// </summary>
/// <remarks>
/// Append-only. There is no public setter, no update path, and the persistence
/// layer refuses modifications and deletions outright rather than merely not
/// offering them — a trail the application can rewrite under some future code
/// path is not a trail, and the cheapest place to make that true is once, here,
/// instead of trusting every caller from now on.
///
/// Entries outlive what they describe. A record deleted in error still has its
/// deletion entry, and that entry is then the only evidence the thing existed —
/// which is exactly what somebody wants when they ask where it went. So the
/// subject is stored as a type name and an id, not as a foreign key that would
/// drag the row into the grave with it.
/// </remarks>
public sealed class AuditEntry
{
    private AuditEntry()
    {
        // EF Core materialisation.
        Action = string.Empty;
        SubjectType = string.Empty;
    }

    private AuditEntry(
        string action,
        string subjectType,
        Guid subjectId,
        Guid? actorId,
        string? actorName,
        IReadOnlyDictionary<string, string?>? before,
        IReadOnlyDictionary<string, string?>? after,
        string? reason,
        DateTimeOffset occurredAt)
    {
        Id = Guid.CreateVersion7();
        Action = action;
        SubjectType = subjectType;
        SubjectId = subjectId;
        ActorId = actorId;
        ActorName = actorName;
        Before = before;
        After = after;
        Reason = reason;
        OccurredAt = occurredAt;
    }

    public Guid Id { get; private init; }

    /// <summary>Dotted and past tense: <c>invoice.paid</c>, <c>employee.hired</c>.</summary>
    public string Action { get; private init; }

    /// <summary>The subject's type name, kept as text so the entry survives it.</summary>
    public string SubjectType { get; private init; }

    public Guid SubjectId { get; private init; }

    /// <summary>Null when the system acted on its own — a scheduled job, a webhook.</summary>
    public Guid? ActorId { get; private init; }

    /// <summary>
    /// The actor's name as it was at the time.
    /// </summary>
    /// <remarks>
    /// Copied rather than joined. People leave and accounts are deleted, and an
    /// audit trail that renders "unknown user" for everything somebody did
    /// before they left has lost the only part of the record anybody reads.
    /// </remarks>
    public string? ActorName { get; private init; }

    public IReadOnlyDictionary<string, string?>? Before { get; private init; }

    public IReadOnlyDictionary<string, string?>? After { get; private init; }

    /// <summary>Why, in the actor's words. Often the most useful column here.</summary>
    public string? Reason { get; private init; }

    public DateTimeOffset OccurredAt { get; private init; }

    public static AuditEntry Record(
        string action,
        string subjectType,
        Guid subjectId,
        DateTimeOffset occurredAt,
        Guid? actorId = null,
        string? actorName = null,
        IReadOnlyDictionary<string, string?>? before = null,
        IReadOnlyDictionary<string, string?>? after = null,
        string? reason = null)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            throw new ArgumentException("An audit entry must say what happened.", nameof(action));
        }

        if (string.IsNullOrWhiteSpace(subjectType))
        {
            throw new ArgumentException("An audit entry must say what it happened to.", nameof(subjectType));
        }

        return new AuditEntry(
            action.Trim(),
            subjectType.Trim(),
            subjectId,
            actorId,
            string.IsNullOrWhiteSpace(actorName) ? null : actorName.Trim(),
            before,
            after,
            string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            occurredAt);
    }

    /// <summary>
    /// The fields that actually moved, as before and after.
    /// </summary>
    /// <remarks>
    /// Only keys present on either side, and only where the value changed. A row
    /// listing twenty identical fields buries the one that did.
    /// </remarks>
    public IReadOnlyDictionary<string, (string? From, string? To)> Changes()
    {
        var before = Before ?? new Dictionary<string, string?>();
        var after = After ?? new Dictionary<string, string?>();

        var changes = new Dictionary<string, (string?, string?)>();

        foreach (var key in before.Keys.Union(after.Keys))
        {
            before.TryGetValue(key, out var from);
            after.TryGetValue(key, out var to);

            if (!string.Equals(from, to, StringComparison.Ordinal))
            {
                changes[key] = (from, to);
            }
        }

        return changes;
    }
}
