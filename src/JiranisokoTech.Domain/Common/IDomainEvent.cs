namespace JiranisokoTech.Domain.Common;

/// <summary>
/// A fact about something that has already happened.
/// </summary>
/// <remarks>
/// Named in the past tense — EmployeeHired, InvoicePaid — because an event is a
/// record, not an instruction. Anything that reads as a command belongs in a
/// service.
///
/// Events carry identifiers and values, never entities. They are queued, and a
/// loaded object graph that crosses that boundary arrives stale, half-tracked,
/// or not at all.
/// </remarks>
public interface IDomainEvent
{
    /// <summary>When the thing happened, not when the handler got round to it.</summary>
    DateTimeOffset OccurredAt { get; }
}
