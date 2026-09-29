using JiranisokoTech.Application.Payroll;
using JiranisokoTech.Application.People;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Domain.Payroll;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Tests.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JiranisokoTech.Tests.Payroll;

/// <summary>
/// A period's pay, drafted from the staff records, agreed, and paid.
/// </summary>
/// <remarks>
/// <b>These exist because nothing tested a pay run.</b> The statutory rates had eight tests of
/// their arithmetic, and the run that uses them — the thing that decides what lands in
/// somebody's account — had none: not the draft, not proration, not the redraft after a
/// correction, not the refusal to move once approved. An audit of the implementation against
/// the brief found that, and it is the kind of gap that stays invisible until a payslip is
/// wrong.
///
/// Through the real application and database, because a run is assembled by queries over
/// employment, leave and holidays, and a test over hand-built objects would test the part least
/// likely to be wrong. The database is shared by the class, so every test uses its own period
/// and asserts on its own person's payslip.
/// </remarks>
public class PayRunTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    private const long Salary = 150_000_00;

    [Fact]
    public async Task Somebody_employed_all_month_is_paid_their_salary()
    {
        var (start, end) = Month(2025, 3);
        var person = await SomebodyAsync(joined: start.AddYears(-1));

        await factory.InScopeAsync(async services =>
        {
            var (run, _) = await services.GetRequiredService<PayrollService>()
                .DraftAsync(start, end, "KES");

            var slip = Assert.Single(run.Payslips, one => one.EmployeeId == person);

            Assert.Equal(Money.Of(Salary, "KES"), slip.Gross);
        });
    }

    /// <summary>
    /// Somebody who joins partway through is paid for the working days they were here.
    /// </summary>
    /// <remarks>
    /// April 2025 has 22 weekdays; joining on Tuesday the 15th leaves 12 of them, so the gross
    /// is twelve twenty-seconds of the salary. Worked out by hand rather than by calling the
    /// same working-day counter the service uses, which would only prove it agrees with itself.
    /// The rates table for the period may add public holidays, so none are recorded for it here.
    /// </remarks>
    [Fact]
    public async Task Somebody_who_joins_partway_through_is_paid_for_the_days_they_were_here()
    {
        var (start, end) = Month(2025, 4);
        var person = await SomebodyAsync(joined: new DateOnly(2025, 4, 15));

        await factory.InScopeAsync(async services =>
        {
            var (run, _) = await services.GetRequiredService<PayrollService>()
                .DraftAsync(start, end, "KES");

            var slip = Assert.Single(run.Payslips, one => one.EmployeeId == person);

            Assert.Equal(Money.Of(Salary, "KES").Multiply(12m / 22m), slip.Gross);
            Assert.Contains(slip.Lines, line => line.Name == "Part period");
        });
    }

    /// <summary>
    /// Drafting the period again after a salary correction replaces the payslip.
    /// </summary>
    /// <remarks>
    /// The redraft clears the run's payslips in place, and a collection cleared in place is
    /// the shape CLAUDE.md records as having lost rows silently before. Asserted on what is in
    /// the database afterwards: one payslip for this person, at the corrected figure.
    /// </remarks>
    [Fact]
    public async Task Drafting_again_after_a_correction_replaces_the_payslip()
    {
        var (start, end) = Month(2025, 5);
        var person = await SomebodyAsync(joined: start.AddYears(-1));

        await factory.InScopeAsync(services =>
            services.GetRequiredService<PayrollService>().DraftAsync(start, end, "KES"));

        await factory.InScopeAsync(services =>
            services.GetRequiredService<PeopleService>().AgreeTermsAsync(person, Terms(Salary + 10_000_00)));

        Guid runId = default;

        await factory.InScopeAsync(async services =>
            runId = (await services.GetRequiredService<PayrollService>()
                .DraftAsync(start, end, "KES")).Run.Id);

        await factory.InScopeAsync(async services =>
        {
            var stored = await services.GetRequiredService<AppDbContext>().PayRuns
                .AsNoTracking()
                .Include(run => run.Payslips)
                .SingleAsync(run => run.Id == runId);

            var slip = Assert.Single(stored.Payslips, one => one.EmployeeId == person);

            Assert.Equal(Money.Of(Salary + 10_000_00, "KES"), slip.Gross);
        });
    }

    [Fact]
    public async Task An_approved_run_is_paid_and_can_no_longer_be_redrafted()
    {
        var (start, end) = Month(2025, 6);
        await SomebodyAsync(joined: start.AddYears(-1));

        Guid runId = default;

        await factory.InScopeAsync(async services =>
        {
            var payroll = services.GetRequiredService<PayrollService>();

            runId = (await payroll.DraftAsync(start, end, "KES")).Run.Id;

            await payroll.ApproveAsync(runId, Guid.CreateVersion7());
            await payroll.PayAsync(runId);
        });

        await factory.InScopeAsync(async services =>
        {
            var stored = await services.GetRequiredService<AppDbContext>().PayRuns
                .AsNoTracking()
                .SingleAsync(run => run.Id == runId);

            Assert.Equal(PayRunStatus.Paid, stored.Status);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                services.GetRequiredService<PayrollService>().DraftAsync(start, end, "KES"));
        });
    }

    /// <summary>
    /// Somebody paid in another currency is left out, and the run says who and why.
    /// </summary>
    [Fact]
    public async Task Somebody_paid_in_another_currency_is_left_out_by_name()
    {
        var (start, end) = Month(2025, 7);
        var person = await SomebodyAsync(joined: start.AddYears(-1), currency: "USD");

        await factory.InScopeAsync(async services =>
        {
            var (run, skipped) = await services.GetRequiredService<PayrollService>()
                .DraftAsync(start, end, "KES");

            Assert.DoesNotContain(run.Payslips, one => one.EmployeeId == person);
            Assert.Contains(skipped, line => line.Contains("paid in USD"));
        });
    }

    private async Task<Guid> SomebodyAsync(DateOnly joined, string currency = "KES")
    {
        Guid id = default;

        await factory.InScopeAsync(async services =>
        {
            var people = services.GetRequiredService<PeopleService>();

            var person = await people.HireAsync(
                "Payee " + Guid.CreateVersion7().ToString("N")[^8..], joined);

            await people.StartAsync(person.Id);
            await people.AgreeTermsAsync(person.Id, Terms(Salary, currency));

            id = person.Id;
        });

        return id;
    }

    private static EmploymentTerms Terms(long salary, string currency = "KES") => new()
    {
        SalaryMinorUnits = salary,
        SalaryCurrency = currency,
        Frequency = PayFrequency.Monthly,
    };

    private static (DateOnly Start, DateOnly End) Month(int year, int month)
    {
        var start = new DateOnly(year, month, 1);

        return (start, start.AddMonths(1).AddDays(-1));
    }
}
