using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.People;

/*
 * Facts about people, in the past tense, carrying ids and values.
 *
 * Each one exists because something outside this module has to react to it: an
 * account to open or close, a handover to start, a capacity figure to recompute,
 * a line manager to tell. None of that belongs in the entity that raised it.
 */

/// <summary>Somebody has a record and a start date. They may not have started yet.</summary>
public sealed record EmployeeHired(
    Guid EmployeeId,
    string FullName,
    Guid? DepartmentId,
    DateOnly StartsOn) : DomainEvent;

/// <summary>Their first day has come.</summary>
public sealed record EmployeeStarted(Guid EmployeeId, string FullName) : DomainEvent;

/// <summary>
/// They have left.
/// </summary>
/// <remarks>
/// Carries who reported to them, because that is the urgent part: those people
/// now answer to nobody, and whoever handles this has to place them before the
/// next round of work is assigned.
/// </remarks>
public sealed record EmployeeLeft(
    Guid EmployeeId,
    string FullName,
    DateOnly On,
    string Reason) : DomainEvent;

/// <summary>Stopped, but still employed.</summary>
public sealed record EmployeeSuspended(Guid EmployeeId, string Reason) : DomainEvent;

/// <summary>Back, from suspension or from having left.</summary>
public sealed record EmployeeReinstated(Guid EmployeeId) : DomainEvent;

/// <summary>Moved between departments, or placed in one for the first time.</summary>
public sealed record EmployeeMoved(
    Guid EmployeeId,
    Guid? FromDepartmentId,
    Guid? ToDepartmentId) : DomainEvent;

/// <summary>Who they answer to has changed.</summary>
public sealed record ReportingLineChanged(
    Guid EmployeeId,
    Guid? FromManagerId,
    Guid? ToManagerId) : DomainEvent;

/// <summary>An account was attached to this person, or taken away.</summary>
public sealed record EmployeeAccountLinked(Guid EmployeeId, Guid AccountId) : DomainEvent;

public sealed record DepartmentOpened(Guid DepartmentId, string Name, string Slug) : DomainEvent;

/// <summary>
/// A department has a new head, or none.
/// </summary>
/// <remarks>
/// The reason this is an event rather than a column change nobody watches: in
/// the system this replaces, being a department head is what grants the role,
/// and losing the post is what takes it away. Revoking a role has to end
/// sessions and write an audit row, neither of which belongs inside a setter.
/// </remarks>
public sealed record DepartmentHeadChanged(
    Guid DepartmentId,
    Guid? FromEmployeeId,
    Guid? ToEmployeeId) : DomainEvent;

public sealed record DepartmentClosed(Guid DepartmentId) : DomainEvent;

public sealed record DepartmentReopened(Guid DepartmentId) : DomainEvent;

/// <summary>
/// Somebody's pay or terms changed.
/// </summary>
/// <remarks>
/// Carries no amounts, deliberately. An outbox row is JSON in a table with its own
/// retention and its own readers, and a salary does not belong in two places. What the
/// event is for is telling the rest of the system that the terms moved — payroll,
/// when it exists, and anything watching contract types — not for carrying the figure.
/// </remarks>
public sealed record EmployeeTermsChanged(
    Guid EmployeeId,
    string FullName,
    ContractType? Contract,
    PayFrequency? Frequency) : DomainEvent;
