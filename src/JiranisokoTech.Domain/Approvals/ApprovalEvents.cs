using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Approvals;

public sealed record ApprovalRequested(
    Guid ApprovalId,
    string SubjectType,
    Guid SubjectId,
    string Action,
    Guid RequestedById,
    int Steps) : DomainEvent;

public sealed record ApprovalStepDecided(
    Guid ApprovalId,
    int Order,
    Guid? DecidedById,
    StepStatus Status,
    string? Note) : DomainEvent;

public sealed record ApprovalStepReassigned(
    Guid ApprovalId,
    int Order,
    Guid FromEmployeeId,
    Guid ToEmployeeId,
    DateTimeOffset At) : DomainEvent;

/// <summary>
/// The chain has finished, one way or another.
/// </summary>
/// <remarks>
/// This is the event the rest of the system waits on. Whatever raised the
/// request — a requisition, a leave day, an expense — subscribes to this and
/// acts, which is what keeps the approval engine from needing to know anything
/// about any of them.
/// </remarks>
public sealed record ApprovalSettled(
    Guid ApprovalId,
    string SubjectType,
    Guid SubjectId,
    string Action,
    ApprovalStatus Status,
    string? Outcome,
    DateTimeOffset At,
    // Carried on the event rather than looked up. A subscriber running from the
    // outbox may be handling this minutes later, and the request it would have
    // read could by then have been swept or changed.
    Guid RequestedById) : DomainEvent;
