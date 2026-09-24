using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Payroll;
using JiranisokoTech.Domain.Time;
using Money = JiranisokoTech.Domain.Common.Money;

namespace JiranisokoTech.Application.Payroll;

/// <summary>What the payroll needs to read and write.</summary>
public interface IPayrollRepository
{
    Task<PayRun?> FindRunAsync(Guid id, CancellationToken cancellationToken = default);

    Task<List<PayRun>> RunsAsync(CancellationToken cancellationToken = default);

    /// <summary>A run already covering this period, if one is.</summary>
    /// <remarks>
    /// So that drafting March twice is refused with a sentence naming the run that already
    /// exists, rather than producing two runs nobody can tell apart and a doubled cost in the
    /// accounts.
    /// </remarks>
    Task<PayRun?> RunForAsync(
        DateOnly periodStart, DateOnly periodEnd, CancellationToken cancellationToken = default);

    void Add(PayRun run);

    /// <summary>Everybody who was employed for any part of the period.</summary>
    Task<List<Employee>> EmployedDuringAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    /// <summary>The rates in force for a period, or null when none have been recorded.</summary>
    Task<StatutoryRates?> RatesOnAsync(
        DateOnly on, CancellationToken cancellationToken = default);

    /// <summary>Every set that has been recorded, most recent first.</summary>
    Task<List<StatutoryRates>> AllRatesAsync(CancellationToken cancellationToken = default);

    void AddRates(StatutoryRates rates);

    /// <summary>Working days of unpaid leave each person took inside the period.</summary>
    Task<Dictionary<Guid, int>> UnpaidLeaveDaysAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    Task<IReadOnlySet<DateOnly>> HolidaysBetweenAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    Task SaveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Drafting, agreeing and paying a period's pay.
/// </summary>
/// <remarks>
/// Section 22, and the last gap in section 94's finance chain — the income-and-expenditure
/// report calls its bottom line "a difference" and never "profit" because the largest cost the
/// firm has is missing from it.
///
/// <b>A draft is assembled from what the system already knows, and rebuilt rather than
/// patched.</b> This is the one thing payroll here can do that a bureau cannot: it holds the
/// employment terms, the joining and leaving dates, the approved unpaid leave with weekends and
/// public holidays already taken out of it, and the statutory rates in force for the period.
/// Somebody corrects a salary, and the honest response is to draft the period again — patching
/// a figure is how a payslip stops agreeing with the record it came from.
///
/// <b>Where the pay history lives.</b> Nothing in this system keeps a dated history of what
/// somebody earned: <c>Employee.Terms</c> is replaced wholesale by <c>Agree</c>, the audit trail
/// deliberately records that terms changed and not what to, and <c>EmployeeTermsChanged</c>
/// carries no money on purpose. An approved payslip is therefore the only record of what
/// somebody was actually paid in a given month, which is why it freezes and why it is never
/// edited. Redrafting a period that has not been approved uses today's terms, which is correct:
/// a draft describes what the firm owes now.
/// </remarks>
public sealed class PayrollService(IPayrollRepository payroll, IClock clock)
{
    /// <summary>
    /// Build a period's pay from the staff list, or rebuild it.
    /// </summary>
    /// <remarks>
    /// Returns the run and the people it could not include, by name and with the reason —
    /// somebody paid in another currency, somebody on an invoice rather than a salary, somebody
    /// whose terms carry no figure at all. A payroll that silently left people out would be
    /// discovered on payday.
    /// </remarks>
    public async Task<(PayRun Run, IReadOnlyList<string> Skipped)> DraftAsync(
        DateOnly periodStart,
        DateOnly periodEnd,
        string currency,
        CancellationToken cancellationToken = default)
    {
        var run = await payroll.RunForAsync(periodStart, periodEnd, cancellationToken);

        if (run is null)
        {
            run = PayRun.Draft(periodStart, periodEnd, currency, clock.Now);
            payroll.Add(run);
        }
        else
        {
            // Rebuilt in place, so the run keeps its identity and anything already pointing at
            // it still does.
            run.Clear();
        }

        var rates = await payroll.RatesOnAsync(periodEnd, cancellationToken);
        var people = await payroll.EmployedDuringAsync(periodStart, periodEnd, cancellationToken);
        var unpaid = await payroll.UnpaidLeaveDaysAsync(periodStart, periodEnd, cancellationToken);
        var holidays = await payroll.HolidaysBetweenAsync(periodStart, periodEnd, cancellationToken);

        var skipped = new List<string>();

        foreach (var person in people.OrderBy(one => one.FullName, StringComparer.Ordinal))
        {
            var reason = WhyNot(person, run.Currency);

            if (reason is not null)
            {
                skipped.Add($"{person.FullName} — {reason}");
                continue;
            }

            run.Add(Slip(person, run, rates, unpaid, holidays));
        }

        await payroll.SaveAsync(cancellationToken);

        return (run, skipped);
    }

    /// <summary>
    /// Agree the figures. Nothing moves afterwards.
    /// </summary>
    public async Task ApproveAsync(
        Guid runId, Guid approvedBy, CancellationToken cancellationToken = default)
    {
        var run = await Required(runId, cancellationToken);

        foreach (var slip in run.Payslips)
        {
            slip.RefuseIfItTakesMoreThanItPays();
        }

        run.Approve(approvedBy, clock.Now);

        await payroll.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// The money has left the account.
    /// </summary>
    /// <remarks>
    /// Separate from approving for the reason paying an expense claim is separate from
    /// approving one: approval says the figures are right, payment says the money has gone, and
    /// one person may be allowed to do the first and not the second.
    /// </remarks>
    public async Task PayAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await Required(runId, cancellationToken);

        run.Pay(clock.Now);

        await payroll.SaveAsync(cancellationToken);
    }

    public async Task AbandonAsync(
        Guid runId, string why, CancellationToken cancellationToken = default)
    {
        var run = await Required(runId, cancellationToken);

        run.Abandon(why);

        await payroll.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Why somebody is not in this run, or null when they are.
    /// </summary>
    /// <remarks>
    /// Named reasons rather than a filter, because every one of these is something a person
    /// should look at. Somebody with no salary recorded is usually an oversight on the staff
    /// record rather than somebody who works for nothing.
    /// </remarks>
    private static string? WhyNot(Employee person, string currency)
    {
        if (person.Terms.Frequency == PayFrequency.OnInvoice)
        {
            return "paid against an invoice rather than on a cycle";
        }

        if (person.Terms.Salary is not { } salary)
        {
            return "no salary is recorded on their staff record";
        }

        if (!string.Equals(salary.Currency, currency, StringComparison.Ordinal))
        {
            return $"paid in {salary.Currency}, and this run is in {currency}";
        }

        return null;
    }

    /// <summary>
    /// One person's payslip: their pay for the part of the period they worked, less what the
    /// rates take off it.
    /// </summary>
    private Payslip Slip(
        Employee person,
        PayRun run,
        StatutoryRates? rates,
        IReadOnlyDictionary<Guid, int> unpaid,
        IReadOnlySet<DateOnly> holidays)
    {
        var salary = person.Terms.Salary!.Value;

        var from = Later(run.PeriodStart, person.StartsOn);
        var to = person.LeftOn is { } left ? Earlier(run.PeriodEnd, left) : run.PeriodEnd;

        var worked = WorkingDays.Between(from, to, holidays);
        var whole = WorkingDays.Between(run.PeriodStart, run.PeriodEnd, holidays);

        var gross = Pro(salary, worked, whole, out var prorated);
        var slip = Payslip.For(person.Id, gross, from, to);

        if (prorated)
        {
            slip.Take(
                "Part period",
                Money.Zero(gross.Currency),
                DeductionKind.Agreed,
                $"{worked} of {whole} working days, joined or left inside the period");
        }

        /*
         * Unpaid leave is taken off as its own line rather than folded into the gross, so the
         * payslip says why somebody was paid less than their salary. A smaller number with no
         * explanation is the thing people come and ask about.
         */
        if (unpaid.TryGetValue(person.Id, out var days) && days > 0 && whole > 0)
        {
            var aDay = gross.Multiply(1m / whole);

            slip.Take(
                "Unpaid leave",
                aDay.Times(Math.Min(days, whole)),
                DeductionKind.Agreed,
                $"{days} working {(days == 1 ? "day" : "days")} at {aDay} a day");
        }

        if (rates is not null)
        {
            foreach (var line in rates.Deductions(gross))
            {
                slip.Take(
                    line.Name, Money.Of(line.MinorUnits, gross.Currency), line.Kind, line.Basis);
            }
        }

        return slip;
    }

    /// <summary>
    /// A part period's share of a salary.
    /// </summary>
    /// <remarks>
    /// By working days rather than by calendar days, and it matters more than it looks: a
    /// joiner starting on the first Monday of a month that began on a Saturday has worked every
    /// working day there was, and a calendar-day share would quietly pay them for less than a
    /// full month.
    /// </remarks>
    private static Money Pro(Money salary, int worked, int whole, out bool prorated)
    {
        prorated = whole > 0 && worked < whole;

        if (!prorated || whole == 0)
        {
            return salary;
        }

        return salary.Multiply((decimal)worked / whole);
    }

    private static DateOnly Later(DateOnly left, DateOnly right) => left > right ? left : right;

    private static DateOnly Earlier(DateOnly left, DateOnly right) => left < right ? left : right;

    private async Task<PayRun> Required(Guid id, CancellationToken cancellationToken) =>
        await payroll.FindRunAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("That pay run no longer exists.");
}
