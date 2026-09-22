using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Work;

/*
 * Facts about work. Each one exists because something outside this module has
 * to react: somebody to tell, a figure to recompute, a client update to write.
 */

public sealed record WorkItemRaised(
    Guid WorkItemId,
    string Title,
    Guid? ProjectId,
    Guid RaisedById) : DomainEvent;

/// <summary>
/// Work has changed hands.
/// </summary>
/// <remarks>
/// Carries both ends, because the person losing it needs telling as much as the
/// person gaining it — an item that quietly left somebody's list is the most
/// common way work is dropped.
/// </remarks>
public sealed record WorkItemAssigned(
    Guid WorkItemId,
    string Title,
    Guid? FromEmployeeId,
    Guid? ToEmployeeId) : DomainEvent;

public sealed record WorkItemMoved(
    Guid WorkItemId,
    WorkItemStatus From,
    WorkItemStatus To,
    string? Because) : DomainEvent;

/// <summary>
/// Finished and accepted.
/// </summary>
/// <remarks>
/// Separate from the general move, because far more things care about this one
/// than about any other transition, and making them all filter a status enum is
/// how a subscriber ends up reacting to the wrong one.
/// </remarks>
public sealed record WorkItemCompleted(
    Guid WorkItemId,
    string Title,
    Guid? AssigneeId,
    Guid? ProjectId,
    DateTimeOffset At) : DomainEvent;

public sealed record ProjectStarted(Guid ProjectId, string Name, string Code) : DomainEvent;

public sealed record ProjectStatusChanged(
    Guid ProjectId,
    string Name,
    ProjectStatus To,
    DateTimeOffset At,
    string? Because = null) : DomainEvent;

public sealed record ProjectLeadChanged(
    Guid ProjectId,
    Guid? FromEmployeeId,
    Guid? ToEmployeeId) : DomainEvent;
