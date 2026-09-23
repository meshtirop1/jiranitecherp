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

        /*
         * Past its date and still open — the second half of that read as "not
         * Done and not Cancelled" until work gained a released state, at which
         * point every item the firm had shipped late went on being counted as
         * late work nobody had finished. The page says "items are past the date
         * they were due" and sends the reader to the board to do something about
         * them; there is nothing to do about a released one.
         */
        var workOverdue = await database.WorkItems
            .AsNoTracking()
            .CountAsync(
                item => item.DueOn != null
                    && item.DueOn < today
                    && !WorkItem.Finished.Contains(item.Status),
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
    /// Money refuses to add across currencies, which is correct: a total that
    /// silently adds shillings to dollars is worse than no total, because
    /// somebody acts on it.
    ///
    /// The first version of this returned null for that case AND for having
    /// nothing to add, and its own comment claimed the page would "say so
    /// instead of lying". The page could not — both arrived as null, so it
    /// rendered "Nothing is outstanding. Every invoice sent has been paid." for
    /// a firm holding unpaid invoices in two currencies. False, and false in
    /// the direction that stops somebody chasing money.
    ///
    /// So the two are different answers now and the caller has to handle both.
    /// </remarks>
    private static Tally Total(IEnumerable<Money> amounts)
    {
        var list = amounts.ToList();

        if (list.Count == 0)
        {
            return Tally.Nothing;
        }

        var currency = list[0].Currency;

        return list.Any(amount => amount.Currency != currency)
            ? Tally.AcrossCurrencies
            : new Tally(list.Aggregate(Money.Zero(currency), (running, amount) => running + amount));
    }
}

/// <summary>
/// A total, or the reason there is not one.
/// </summary>
/// <remarks>
/// Three states, because "nothing to add" and "cannot be added" are different
/// facts and only one of them is reassuring. A page given a bare null cannot
/// tell them apart and will pick the wrong sentence.
/// </remarks>
public readonly record struct Tally(Money? Amount, bool Mixed = false)
{
    public static Tally Nothing => new(null);

    public static Tally AcrossCurrencies => new(null, true);

    /// <summary>There is a figure, and it can be shown.</summary>
    public bool HasFigure => Amount is not null;
}

/// <summary>Where the firm stands, in the figures somebody acts on.</summary>
public sealed record FirmState(
    Tally Outstanding,
    Tally Overdue,
    int OverdueCount,
    DateOnly? OldestOverdue,
    int UnbilledMinutes,
    int UnapprovedMinutes,
    int UnapprovedEntries,
    Tally OwedToStaff,
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
