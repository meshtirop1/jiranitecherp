using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Recruitment;

public sealed record RequisitionRaised(
    Guid RequisitionId,
    string JobTitle,
    Guid? DepartmentId,
    int Headcount,
    Guid RaisedById) : DomainEvent;

/// <summary>
/// Sent for approval.
/// </summary>
/// <remarks>
/// This is what opens the approval chain. The requisition does not know how it
/// will be approved or by whom; something listening to this decides that, which
/// is what lets the rules about who approves a hire change without touching
/// recruitment at all.
/// </remarks>
public sealed record RequisitionSubmitted(
    Guid RequisitionId,
    string JobTitle,
    Guid RaisedById,
    DateTimeOffset At) : DomainEvent;

public sealed record RequisitionApproved(
    Guid RequisitionId,
    string JobTitle,
    Guid? DepartmentId,
    int Headcount,
    DateTimeOffset At) : DomainEvent;

public sealed record RequisitionRefused(
    Guid RequisitionId,
    string JobTitle,
    Guid RaisedById,
    string Reason,
    DateTimeOffset At) : DomainEvent;

public sealed record RequisitionFilled(
    Guid RequisitionId,
    string JobTitle,
    int Headcount,
    DateTimeOffset At) : DomainEvent;

public sealed record RequisitionHeadcountChanged(
    Guid RequisitionId,
    int Headcount,
    int HiredCount,
    DateTimeOffset At) : DomainEvent;

public sealed record RequisitionClosed(
    Guid RequisitionId,
    string JobTitle,
    string Reason,
    DateTimeOffset At) : DomainEvent;

public sealed record PostingPublished(
    Guid PostingId,
    Guid RequisitionId,
    string Title,
    string Slug,
    DateTimeOffset At) : DomainEvent;

public sealed record PostingClosed(
    Guid PostingId,
    Guid RequisitionId,
    string Title,
    DateTimeOffset At) : DomainEvent;

public sealed record ApplicationReceived(
    Guid ApplicationId,
    Guid PostingId,
    Guid CandidateId,
    DateTimeOffset At) : DomainEvent;

/// <summary>
/// An application has changed state.
/// </summary>
/// <remarks>
/// Carries no reason, deliberately. A rejection reason is an internal note
/// about a person, and an event is the thing most likely to end up somewhere it
/// was not meant to go — a notification, a webhook, a log somebody grep.
/// </remarks>
public sealed record ApplicationMoved(
    Guid ApplicationId,
    Guid PostingId,
    Guid CandidateId,
    ApplicationStatus From,
    ApplicationStatus To,
    DateTimeOffset At) : DomainEvent;

/// <summary>
/// Somebody has been hired.
/// </summary>
/// <remarks>
/// Its own event because far more things care about this than about any other
/// move: a post comes off the requisition, an account gets opened, a start date
/// gets agreed. Making each of them filter a status enum is how one of them
/// ends up reacting to the wrong value.
/// </remarks>
public sealed record CandidateHired(
    Guid ApplicationId,
    Guid PostingId,
    Guid CandidateId,
    DateTimeOffset At) : DomainEvent;
