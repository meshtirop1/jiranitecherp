using JiranisokoTech.Domain.Money;
using JiranisokoTech.Domain.Time;
using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Money = JiranisokoTech.Domain.Common.Money;

namespace JiranisokoTech.Infrastructure.Reporting;

/// <summary>
/// The figures the firm is run on.
/// </summary>
/// <remarks>
/// Every figure here answers a question somebody actually asks, and each one has
/// somewhere to go about it. That is the whole design rule: the home page refuses
/// to be a wall of tiles because a count nobody acts on is read twice and then
/// skipped, and a reporting page earns its place only by holding to the same
/// standard. "Sixty-two work items" is a tile. "Fourteen days of approved work
/// nobody has billed" is a question with an answer at the end of it.
///
/// Read in one pass and handed over as one record, because these are read
/// together or not at all, and a page that fires eleven queries to draw eleven
/// numbers gets slower every time somebody adds a twelfth.
/// </remarks>
public sealed class ReportingQueries(AppDbContext database)
{
    public async Task<FirmState> StateAsync(
        DateOnly today, CancellationToken cancellationToken = default)
    {
        var unsettled = await database.Invoices
            .AsNoTracking()
            .Include(invoice => invoice.Lines)
            .Include(invoice => invoice.Payments)
            .Where(invoice => invoice.Status == InvoiceStatus.Sent
                || invoice.Status == InvoiceStatus.PartlyPaid)
            .ToListAsync(cancellationToken);

        var overdue = unsettled.Where(invoice => invoice.DueOn < today).ToList();

        // Approved, billable, and on no invoice. This is work the firm has done
        // and not asked to be paid for, which is the one number here that is
        // purely money left on the table.
        var unbilled = await database.TimeEntries
            .AsNoTracking()
            .Where(entry => entry.IsBillable
                && entry.ApprovedAt != null
                && entry.InvoiceId == null)
            .SumAsync(entry => (int?)entry.Minutes, cancellationToken) ?? 0;

        var unapproved = await database.TimeEntries
            .AsNoTracking()
            .Where(entry => entry.ApprovedAt == null)
            .SumAsync(entry => (int?)entry.Minutes, cancellationToken) ?? 0;

        var unapprovedCount = await database.TimeEntries
            .AsNoTracking()
            .CountAsync(entry => entry.ApprovedAt == null, cancellationToken);

        var owedToStaff = await database.Expenses
            .AsNoTracking()
            .Where(claim => claim.Status == ClaimStatus.Approved)
            .Select(claim => new { claim.MinorUnits, claim.Currency })
            .ToListAsync(cancellationToken);

        var leaveWaiting = await database.Leave
            .AsNoTracking()
            .CountAsync(leave => leave.Status == LeaveStatus.AwaitingApproval, cancellationToken);

        var claimsWaiting = await database.Expenses
            .AsNoTracking()
            .CountAsync(claim => claim.Status == ClaimStatus.AwaitingApproval, cancellationToken);

        var fortnight = today.AddDays(14);

        var awaySoon = await database.Leave
            .AsNoTracking()
            .Where(leave => leave.Status == LeaveStatus.Approved
                && leave.To >= today
                && leave.From <= fortnight)
            .OrderBy(leave => leave.From)
            .Select(leave => new { leave.EmployeeId, leave.Kind, leave.From, leave.To })
            .ToListAsync(cancellationToken);

        var people = await database.Employees
            .AsNoTracking()
            .ToDictionaryAsync(person => person.Id, person => person.FullName, cancellationToken);

        var projectsRunning = await database.Projects
            .AsNoTracking()
            .CountAsync(
                project => project.Status != ProjectStatus.Delivered
                    && project.Status != ProjectStatus.Cancelled,
                cancellationToken);

        var blocked = await database.WorkItems
            .AsNoTracking()
            .CountAsync(item => item.Status == WorkItemStatus.Blocked, cancellationToken);

        var workOverdue = await database.WorkItems
            .AsNoTracking()
            .CountAsync(
                item => item.DueOn != null
                    && item.DueOn < today
                    && item.Status != WorkItemStatus.Done
                    && item.Status != WorkItemStatus.Cancelled,
                cancellationToken);

        return new FirmState(
            Outstanding: Total(unsettled.Select(invoice => invoice.Outstanding)),
            Overdue: Total(overdue.Select(invoice => invoice.Outstanding)),
            OverdueCount: overdue.Count,
            OldestOverdue: overdue.Count == 0 ? null : overdue.Min(invoice => invoice.DueOn),
            UnbilledMinutes: unbilled,
            UnapprovedMinutes: unapproved,
            UnapprovedEntries: unapprovedCount,
            OwedToStaff: Total(owedToStaff.Select(one => Money.Of(one.MinorUnits, one.Currency))),
            OwedToStaffCount: owedToStaff.Count,
            LeaveWaiting: leaveWaiting,
            ClaimsWaiting: claimsWaiting,
            AwaySoon: [.. awaySoon.Select(leave => new AwayRow(
                people.GetValueOrDefault(leave.EmployeeId) ?? "Somebody who has left",
                leave.Kind,
                leave.From,
                leave.To))],
            ProjectsRunning: projectsRunning,
            WorkBlocked: blocked,
            WorkOverdue: workOverdue);
    }

    /// <summary>
    /// Adds money that may be in more than one currency.
    /// </summary>
    /// <remarks>
    /// Money refuses to add across currencies, which is correct and is why this
    /// returns null rather than a figure when it finds two. A total that
    /// silently adds shillings to dollars is worse than no total: somebody acts
    /// on it. The firm bills in one currency today, so in practice this returns
    /// a figure; the day it does not, the page says so instead of lying.
    /// </remarks>
    private static Money? Total(IEnumerable<Money> amounts)
    {
        var list = amounts.ToList();

        if (list.Count == 0)
        {
            return null;
        }

        var currency = list[0].Currency;

        return list.Any(amount => amount.Currency != currency)
            ? null
            : list.Aggregate(Money.Zero(currency), (running, amount) => running + amount);
    }
}

/// <summary>Where the firm stands, in the figures somebody acts on.</summary>
public sealed record FirmState(
    Money? Outstanding,
    Money? Overdue,
    int OverdueCount,
    DateOnly? OldestOverdue,
    int UnbilledMinutes,
    int UnapprovedMinutes,
    int UnapprovedEntries,
    Money? OwedToStaff,
    int OwedToStaffCount,
    int LeaveWaiting,
    int ClaimsWaiting,
    IReadOnlyList<AwayRow> AwaySoon,
    int ProjectsRunning,
    int WorkBlocked,
    int WorkOverdue)
{
    public bool NothingIsWaiting =>
        UnapprovedEntries == 0 && LeaveWaiting == 0 && ClaimsWaiting == 0;
}

public sealed record AwayRow(string Name, LeaveKind Kind, DateOnly From, DateOnly To);
