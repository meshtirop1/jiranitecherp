using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Time;

/// <summary>
/// An hour of somebody's day, against something.
/// </summary>
/// <remarks>
/// Minutes as an integer, like every other duration here, and for the same
/// reason: a decimal number of hours accumulates rounding across a month and
/// ends up on an invoice somebody has to explain.
///
/// An entry can name a work item, a project, both, or neither. Neither is
/// ordinary — the hour spent on a phone call about nothing in particular is
/// still an hour, and a system that refuses to record it gets a timesheet that
/// adds up to thirty hours a week.
/// </remarks>
public sealed class TimeEntry : Entity, IAuditable
{
    /// <summary>A day has 1440 minutes, and nobody has worked all of them.</summary>
    private const int LongestDay = 16 * 60;

    private TimeEntry()
    {
    }

    private TimeEntry(
        Guid employeeId,
        DateOnly on,
        int minutes,
        Guid? workItemId,
        Guid? projectId,
        string? note,
        bool billable)
    {
        EmployeeId = employeeId;
        On = on;
        Minutes = Within(minutes);
        WorkItemId = workItemId;
        ProjectId = projectId;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        IsBillable = billable;

        Raise(new TimeLogged(Id, employeeId, on, Minutes, projectId));
    }

    public static TimeEntry Log(
        Guid employeeId,
        DateOnly on,
        int minutes,
        Guid? workItemId = null,
        Guid? projectId = null,
        string? note = null,
        bool billable = true) =>
        new(employeeId, on, minutes, workItemId, projectId, note, billable);

    public Guid EmployeeId { get; private init; }

    public DateOnly On { get; private init; }

    public int Minutes { get; private set; }

    public Guid? WorkItemId { get; private set; }

    public Guid? ProjectId { get; private set; }

    public string? Note { get; private set; }

    /// <summary>
    /// Whether this time can appear on an invoice.
    /// </summary>
    /// <remarks>
    /// Recorded when the hour is logged rather than decided later, because the
    /// person who did the work knows, and somebody reconstructing it a month
    /// later at invoicing time does not.
    /// </remarks>
    public bool IsBillable { get; private set; } = true;

    /// <summary>
    /// When a manager agreed it.
    /// </summary>
    /// <remarks>
    /// Approval is what freezes an entry. Before it, the person who logged the
    /// hour can correct it; afterwards they cannot, because it may already have
    /// been billed and a silently edited hour is an invoice that no longer
    /// matches its evidence.
    /// </remarks>
    public DateTimeOffset? ApprovedAt { get; private set; }

    public Guid? ApprovedById { get; private set; }

    /// <summary>Whether it has been put on an invoice.</summary>
    public Guid? InvoiceId { get; private set; }

    public bool IsLocked => ApprovedAt is not null;

    /// <summary>The hours, as somebody would say them.</summary>
    public string Duration => Minutes % 60 == 0
        ? $"{Minutes / 60}h"
        : Minutes < 60 ? $"{Minutes}m" : $"{Minutes / 60}h {Minutes % 60}m";

    public void Amend(int minutes, string? note, bool billable)
    {
        Locked();

        Minutes = Within(minutes);
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        IsBillable = billable;
    }

    public void Against(Guid? workItemId, Guid? projectId)
    {
        Locked();

        WorkItemId = workItemId;
        ProjectId = projectId;
    }

    public void Approve(Guid byEmployeeId, DateTimeOffset at)
    {
        if (byEmployeeId == EmployeeId)
        {
            // Not a rule about trust: a timesheet somebody approves for
            // themselves is not an approval, and every audit of one says so.
            throw new InvalidOperationException("Nobody approves their own time.");
        }

        if (IsLocked)
        {
            return;
        }

        ApprovedAt = at;
        ApprovedById = byEmployeeId;

        Raise(new TimeApproved(Id, EmployeeId, On, Minutes, byEmployeeId));
    }

    /// <summary>
    /// Unlock an entry that was approved in error.
    /// </summary>
    /// <remarks>
    /// Refused once the hour has been invoiced. At that point the figure is on
    /// a document somebody outside this firm is holding, and changing it here
    /// would leave the two disagreeing with nothing to say which is right.
    /// </remarks>
    public void Reopen()
    {
        if (InvoiceId is not null)
        {
            throw new InvalidOperationException(
                "This time is on an invoice. Credit the invoice first, then correct it.");
        }

        ApprovedAt = null;
        ApprovedById = null;
    }

    /// <summary>
    /// Mark this hour as billed.
    /// </summary>
    /// <remarks>
    /// Public rather than internal, because the invoicing service is what calls
    /// it and that lives a layer out. The protection is the two guards below
    /// rather than the accessibility: unapproved or non-billable time cannot
    /// reach an invoice however it is asked.
    /// </remarks>
    public void PutOnInvoice(Guid invoiceId)
    {
        if (!IsLocked)
        {
            throw new InvalidOperationException(
                "Only approved time goes on an invoice. Unapproved hours are a draft.");
        }

        if (!IsBillable)
        {
            throw new InvalidOperationException("This time was recorded as not billable.");
        }

        InvoiceId = invoiceId;
    }

    public void TakeOffInvoice() => InvoiceId = null;

    /// <summary>Nothing here is a secret.</summary>
    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private void Locked()
    {
        if (IsLocked)
        {
            throw new InvalidOperationException(
                "This time has been approved and cannot be changed. Ask for it to be reopened.");
        }
    }

    private static int Within(int minutes) =>
        minutes is < 1 or > LongestDay
            ? throw new ArgumentOutOfRangeException(
                nameof(minutes),
                minutes,
                $"An entry runs from one minute to {LongestDay / 60} hours. Longer than that is "
                + "a mistake, or a day that needs splitting up.")
            : minutes;
}

public sealed record TimeLogged(
    Guid EntryId,
    Guid EmployeeId,
    DateOnly On,
    int Minutes,
    Guid? ProjectId) : DomainEvent;

public sealed record TimeApproved(
    Guid EntryId,
    Guid EmployeeId,
    DateOnly On,
    int Minutes,
    Guid ApprovedById) : DomainEvent;
