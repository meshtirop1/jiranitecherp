using JiranisokoTech.Application.Payroll;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Payroll;
using JiranisokoTech.Domain.Time;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Payroll;

/// <summary>The reads and writes the payroll needs.</summary>
public sealed class PayrollRepository(AppDbContext database) : IPayrollRepository
{
    public Task<PayRun?> FindRunAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.PayRuns
            .Include(run => run.Payslips)
            .ThenInclude(slip => slip.Lines)
            .FirstOrDefaultAsync(run => run.Id == id, cancellationToken);

    public Task<List<PayRun>> RunsAsync(CancellationToken cancellationToken = default) =>
        database.PayRuns
            .AsNoTracking()
            .OrderByDescending(run => run.PeriodStart)
            .ToListAsync(cancellationToken);

    public Task<PayRun?> RunForAsync(
        DateOnly periodStart, DateOnly periodEnd, CancellationToken cancellationToken = default) =>
        database.PayRuns
            .Include(run => run.Payslips)
            .ThenInclude(slip => slip.Lines)
            .FirstOrDefaultAsync(
                run => run.PeriodStart == periodStart
                    && run.PeriodEnd == periodEnd
                    && run.Status != PayRunStatus.Abandoned,
                cancellationToken);

    public void Add(PayRun run) => database.PayRuns.Add(run);

    /// <summary>
    /// Everybody employed for any part of the period.
    /// </summary>
    /// <remarks>
    /// Overlap rather than "active today", which is the whole point. Somebody who left on the
    /// tenth is paid for ten days and is not on the staff list any more; somebody who starts on
    /// the twentieth is not yet active when a run for that month is drafted on the first.
    ///
    /// A suspended person is included, because suspension is not a decision about pay — see
    /// <c>Employee.Suspend</c>, which takes no date and leaves them employed. Whether they are
    /// paid while suspended is a matter for whoever suspended them, and leaving them out of the
    /// run would make that decision silently.
    /// </remarks>
    public Task<List<Employee>> EmployedDuringAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        database.Employees
            .Where(person => person.StartsOn <= to
                && (person.LeftOn == null || person.LeftOn >= from))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// The rates in force for a date.
    /// </summary>
    /// <remarks>
    /// The latest set commencing on or before the date, which is what "in force" means. Matched
    /// against the pay period rather than against today, so that drafting March again in June
    /// uses March's rates — a payroll that recomputed an old month at today's rates would
    /// produce a figure disagreeing with the one already filed.
    /// </remarks>
    public Task<StatutoryRates?> RatesOnAsync(
        DateOnly on, CancellationToken cancellationToken = default) =>
        database.StatutoryRates
            .Include("_bands")
            .Where(rates => rates.InForceFrom <= on)
            .OrderByDescending(rates => rates.InForceFrom)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<List<StatutoryRates>> AllRatesAsync(
        CancellationToken cancellationToken = default) =>
        database.StatutoryRates
            .Include("_bands")
            .AsNoTracking()
            .OrderByDescending(rates => rates.InForceFrom)
            .ToListAsync(cancellationToken);

    public void AddRates(StatutoryRates rates) => database.StatutoryRates.Add(rates);

    /// <summary>
    /// Working days of approved unpaid leave each person took inside the period.
    /// </summary>
    /// <remarks>
    /// Approved only. An unpaid request somebody has asked for and nobody has agreed to is not
    /// a reason to pay them less, and deducting on it would let a request that was later refused
    /// take money off a payslip that had already been approved.
    ///
    /// Requests are clipped to the period. A fortnight of unpaid leave spanning the end of a
    /// month is deducted in two parts, in the months it actually fell in.
    /// </remarks>
    public async Task<Dictionary<Guid, int>> UnpaidLeaveDaysAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var requests = await database.Leave
            .AsNoTracking()
            .Where(request => request.Kind == LeaveKind.Unpaid
                && request.Status == LeaveStatus.Approved
                && request.From <= to
                && request.To >= from)
            .Select(request => new { request.EmployeeId, request.From, request.To })
            .ToListAsync(cancellationToken);

        if (requests.Count == 0)
        {
            return [];
        }

        var holidays = await HolidaysBetweenAsync(from, to, cancellationToken);
        var days = new Dictionary<Guid, int>();

        foreach (var request in requests)
        {
            var start = request.From > from ? request.From : from;
            var end = request.To < to ? request.To : to;

            days[request.EmployeeId] =
                days.GetValueOrDefault(request.EmployeeId)
                + WorkingDays.Between(start, end, holidays);
        }

        return days;
    }

    public async Task<IReadOnlySet<DateOnly>> HolidaysBetweenAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        (await database.Holidays
            .AsNoTracking()
            .Where(holiday => holiday.On >= from && holiday.On <= to)
            .Select(holiday => holiday.On)
            .ToListAsync(cancellationToken))
        .ToHashSet();

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        database.SaveChangesAsync(cancellationToken);
}
