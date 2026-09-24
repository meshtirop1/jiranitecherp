using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.People;

/// <summary>
/// The list of things that have to happen when somebody leaves.
/// </summary>
/// <remarks>
/// Section 9 had a leaver's open work released and nothing else, and the "nothing else"
/// is where the risk is. Recording a departure took three seconds; the laptop, the
/// building pass and the sign-in survived it indefinitely, and nothing anywhere said so.
///
/// This is a checklist rather than an automation, and that is the central decision.
/// Revoking the account could be automatic — the system knows the leaving date and holds
/// the account. It is not, for two reasons. People leave on a date and then work a
/// handover week; an account cut off at midnight on the recorded date locks somebody out
/// mid-sentence. And a leaving date entered wrongly, which happens, would then destroy
/// access with no human having decided anything.
///
/// So each item is something a person marks done, and the record's value is that it says
/// out loud what is still outstanding. A laptop nobody asked for is a laptop nobody
/// misses until the audit.
/// </remarks>
public sealed class Offboarding : Entity, IAuditable
{
    private Offboarding()
    {
    }

    private Offboarding(Guid employeeId, DateOnly leavingOn, DateTimeOffset at)
    {
        EmployeeId = employeeId;
        LeavingOn = leavingOn;
        StartedAt = at;

        Raise(new OffboardingStarted(Id, employeeId, leavingOn, at));
    }

    public static Offboarding Begin(Guid employeeId, DateOnly leavingOn, DateTimeOffset at) =>
        new(employeeId, leavingOn, at);

    public Guid EmployeeId { get; private init; }

    public DateOnly LeavingOn { get; private set; }

    public DateTimeOffset StartedAt { get; private init; }

    /// <summary>When the sign-in was actually revoked, by somebody.</summary>
    public DateTimeOffset? AccessRemovedAt { get; private set; }

    public Guid? AccessRemovedById { get; private set; }

    /// <summary>When the conversation happened, if it happened.</summary>
    public DateTimeOffset? ExitInterviewAt { get; private set; }

    /// <summary>
    /// What they said.
    /// </summary>
    /// <remarks>
    /// Kept, and kept narrowly. An exit interview is where somebody says what they could
    /// not say while employed, and its value depends entirely on it not being read by the
    /// person they were complaining about. It is behind employees.manage and excluded from
    /// the audit trail, which is read more widely.
    /// </remarks>
    public string? ExitInterviewNotes { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public bool IsComplete => CompletedAt is not null;

    /// <summary>
    /// Is there anything left that this record knows about?
    /// </summary>
    /// <remarks>
    /// Only the access, now. This used to include equipment, because a leaver's kit was a list
    /// kept here — and section 15 replaced that with one register the whole firm shares, so
    /// what somebody still holds is a question about the asset register rather than about this
    /// row. The screen asks both and shows them separately, because they are two different
    /// people's jobs.
    /// </remarks>
    public bool HasOutstandingItems => AccessRemovedAt is null;

    public void LeavesOn(DateOnly on) => LeavingOn = on;

    /// <summary>
    /// Record that the sign-in has been closed.
    /// </summary>
    /// <remarks>
    /// Says who did it, because this is the item on the list whose omission is a security
    /// finding rather than an inconvenience, and "somebody dealt with it" is not an answer
    /// to an auditor.
    /// </remarks>
    public void AccessRemoved(Guid byEmployeeId, DateTimeOffset at)
    {
        if (AccessRemovedAt is not null)
        {
            return;
        }

        AccessRemovedAt = at;
        AccessRemovedById = byEmployeeId;

        Raise(new LeaverAccessRemoved(Id, EmployeeId, byEmployeeId, at));
    }

    public void ExitInterviewHeld(string? notes, DateTimeOffset at)
    {
        ExitInterviewAt = at;
        ExitInterviewNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
    }

    /// <summary>
    /// Say the offboarding is finished.
    /// </summary>
    /// <remarks>
    /// Refused while anything is outstanding, which is the only rule in this file with
    /// teeth. A checklist that could be closed with items on it is a checklist that gets
    /// closed with items on it, and the item usually left is the access — the one that
    /// matters most.
    ///
    /// The exit interview is deliberately not required. Somebody may decline one, and a
    /// system that would not let a departure be closed without it would be a system where
    /// somebody types "declined" into a notes field to make a button work.
    /// </remarks>
    public void Complete(DateTimeOffset at)
    {
        if (IsComplete)
        {
            return;
        }

        if (AccessRemovedAt is null)
        {
            throw new InvalidOperationException(
                "Their sign-in has not been closed yet. That is the one item on this list "
                + "whose omission is a security problem rather than an inconvenience.");
        }

        /*
         * Equipment is no longer checked here, and that is not a relaxation. It used to be a
         * list on this row; it is now the asset register, which this aggregate has no business
         * reaching into — so PeopleService checks it before calling this and names the tags
         * that are still out. A rule in one place rather than half a rule in two.
         */
        CompletedAt = at;

        Raise(new OffboardingCompleted(Id, EmployeeId, at));
    }

    /// <summary>
    /// The interview notes never reach the audit trail.
    /// </summary>
    /// <remarks>
    /// An exit interview is where somebody says what they could not say while employed.
    /// Its whole value depends on not being read by the person they were describing, and
    /// the trail is read by everybody holding audit.view.
    /// </remarks>
    public static IReadOnlySet<string> AuditExcludes { get; } =
        new HashSet<string> { nameof(ExitInterviewNotes) };
}

public sealed record OffboardingStarted(
    Guid OffboardingId,
    Guid EmployeeId,
    DateOnly LeavingOn,
    DateTimeOffset At) : DomainEvent;

/// <summary>
/// A leaver's sign-in has been closed.
/// </summary>
/// <remarks>
/// Its own event because it is the one item whose omission is a security finding, and
/// because anything watching for that — a notification, a report, an integration that
/// removes them from other systems — needs to hear it said rather than inferred.
/// </remarks>
public sealed record LeaverAccessRemoved(
    Guid OffboardingId,
    Guid EmployeeId,
    Guid ByEmployeeId,
    DateTimeOffset At) : DomainEvent;

public sealed record OffboardingCompleted(
    Guid OffboardingId, Guid EmployeeId, DateTimeOffset At) : DomainEvent;

