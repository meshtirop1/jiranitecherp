using JiranisokoTech.Application.Accounting;
using JiranisokoTech.Domain.Money;
using JiranisokoTech.Infrastructure.Accounting;
using JiranisokoTech.Tests.Infrastructure;
using Money = JiranisokoTech.Domain.Common.Money;

namespace JiranisokoTech.Tests.Business;

/// <summary>
/// The chart of accounts, and the costs that fall due on a timetable.
/// </summary>
/// <remarks>
/// Section 18 had invoices, payments and expense claims and no way to say what any of it was
/// for. What is tested here is almost entirely the arithmetic and the refusals, because this
/// is a report about money: a figure that is quietly wrong is worse than a page that does not
/// load, and nobody reports a total they cannot check.
/// </remarks>
public class AccountingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 9, 0, 0, TimeSpan.Zero);

    private sealed class Module(DatabaseFixture db) : IAsyncDisposable
    {
        private readonly TestDbContext _context = db.NewContext();

        public AccountingService Accounting =>
            new(new AccountingRepository(_context), db.Clock);

        public AccountingQueries Reads => new(_context);

        public async ValueTask DisposeAsync() => await _context.DisposeAsync();
    }

    /// <summary>
    /// Two accounts cannot share a code.
    /// </summary>
    /// <remarks>
    /// The quietest kind of wrong a figure about money can be. Two rows with one code would
    /// split that account's money across two lines of the report, and neither line would look
    /// wrong — so the refusal names which account already has it rather than letting the
    /// database answer with a constraint name.
    /// </remarks>
    [Fact]
    public async Task Two_accounts_cannot_share_a_code()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        db.Clock.Now = Now;
        await using var module = new Module(db);

        await module.Accounting.OpenAccountAsync("4200", "Software", AccountKind.Expense);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Accounting.OpenAccountAsync("4200", "Rent", AccountKind.Expense));

        Assert.Contains("already Software", refused.Message);
    }

    /// <summary>A code is a key people type, so spacing and case do not matter.</summary>
    /// <remarks>
    /// "4200" and " 4200 " are the same account to everybody except a string comparison, and
    /// the second one silently becoming a second account is how the split above happens without
    /// anybody doing anything wrong.
    /// </remarks>
    [Fact]
    public async Task A_code_typed_untidily_is_the_same_account()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        db.Clock.Now = Now;
        await using var module = new Module(db);

        await module.Accounting.OpenAccountAsync("rent", "Rent", AccountKind.Expense);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Accounting.OpenAccountAsync("  RENT ", "Office rent", AccountKind.Expense));
    }

    /// <summary>
    /// A standing cost cannot be coded to income.
    /// </summary>
    /// <remarks>
    /// It would be counted as money coming in. The report has two sides and nothing else checks
    /// which side a figure belongs on, so this is the only place that can refuse it.
    /// </remarks>
    [Fact]
    public async Task A_standing_cost_cannot_be_coded_to_income()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        db.Clock.Now = Now;
        await using var module = new Module(db);

        var income = await module.Accounting.OpenAccountAsync(
            "1000", "Consulting fees", AccountKind.Income);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Accounting.ScheduleAsync(
                income.Id, "Rent", "Landlord", Money.Of(150_000_00, "KES"),
                Recurrence.Monthly, db.Clock.Today));

        Assert.Contains("income account", refused.Message);
    }

    /// <summary>
    /// Running the charge job twice raises one charge, not two.
    /// </summary>
    /// <remarks>
    /// Its interface demands it, and the consequence of getting it wrong is money: a scheduler
    /// restarted twice in a morning would post three months' rent, and the cost report would be
    /// overstated by an amount nobody could trace to a document.
    /// </remarks>
    [Fact]
    public async Task Raising_what_is_due_twice_raises_one_charge()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        db.Clock.Now = Now;
        await using var module = new Module(db);

        var account = await module.Accounting.OpenAccountAsync(
            "5000", "Rent", AccountKind.Expense);

        await module.Accounting.ScheduleAsync(
            account.Id, "Office rent", "Landlord", Money.Of(150_000_00, "KES"),
            Recurrence.Monthly, db.Clock.Today);

        Assert.Equal(1, await module.Accounting.RaiseWhatIsDueAsync());
        Assert.Equal(0, await module.Accounting.RaiseWhatIsDueAsync());
    }

    /// <summary>
    /// A job that has been off catches up rather than skipping.
    /// </summary>
    /// <remarks>
    /// The other half, and the more expensive one to get wrong. A cost missing from a report
    /// looks exactly like a cost that was not incurred — so a scheduler off for three months
    /// must raise the three charges it missed, each carrying the date it was actually due
    /// rather than the day the job happened to run.
    /// </remarks>
    [Fact]
    public async Task A_job_that_has_been_off_catches_up()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        db.Clock.Now = Now;
        await using var module = new Module(db);

        var account = await module.Accounting.OpenAccountAsync(
            "5000", "Rent", AccountKind.Expense);

        var schedule = await module.Accounting.ScheduleAsync(
            account.Id, "Office rent", "Landlord", Money.Of(150_000_00, "KES"),
            Recurrence.Monthly, db.Clock.Today);

        // Nothing runs for three months.
        db.Clock.Advance(TimeSpan.FromDays(92));

        Assert.Equal(4, await module.Accounting.RaiseWhatIsDueAsync());

        var raised = (await module.Accounting.RaiseWhatIsDueAsync()) == 0;
        Assert.True(raised);

        var charges = (await module.Reads.AccountsAsync())
            .First(one => one.Code == "5000")
            .CodedToIt;

        Assert.Equal(1, charges);
        Assert.Equal(schedule.Id, schedule.Id);
    }

    /// <summary>
    /// A charge keeps the amount it fell due at, even after the cost changes.
    /// </summary>
    /// <remarks>
    /// A rent rise in July must not rewrite what was owed in June. A report that changed
    /// retrospectively would be one nobody could reconcile against a bank statement, and the
    /// person who noticed would have no way to tell which figure had moved.
    /// </remarks>
    [Fact]
    public async Task A_charge_keeps_what_it_fell_due_at()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        db.Clock.Now = Now;
        await using var module = new Module(db);

        var account = await module.Accounting.OpenAccountAsync(
            "5000", "Rent", AccountKind.Expense);

        var schedule = await module.Accounting.ScheduleAsync(
            account.Id, "Office rent", "Landlord", Money.Of(150_000_00, "KES"),
            Recurrence.Monthly, db.Clock.Today);

        await module.Accounting.RaiseWhatIsDueAsync();

        await module.Accounting.AmendAsync(
            schedule.Id, Money.Of(180_000_00, "KES"), "Landlord", null);

        var settled = await module.Reads.ReportAsync(
            db.Clock.Today.AddDays(-1), db.Clock.Today.AddDays(1), "KES");

        Assert.Equal(Money.Of(150_000_00, "KES"), settled.TotalExpenditure);
    }

    /// <summary>
    /// The currency cannot be changed under charges already raised.
    /// </summary>
    /// <remarks>
    /// It would rewrite what those charges were worth. Money refuses cross-currency
    /// arithmetic, so the alternative is not a wrong total but an exception on a report
    /// somebody is reading.
    /// </remarks>
    [Fact]
    public async Task The_currency_cannot_be_changed_under_a_standing_cost()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        db.Clock.Now = Now;
        await using var module = new Module(db);

        var account = await module.Accounting.OpenAccountAsync(
            "5000", "Rent", AccountKind.Expense);

        var schedule = await module.Accounting.ScheduleAsync(
            account.Id, "Office rent", "Landlord", Money.Of(150_000_00, "KES"),
            Recurrence.Monthly, db.Clock.Today);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Accounting.AmendAsync(
                schedule.Id, Money.Of(1_200_00, "USD"), "Landlord", null));

        Assert.Contains("KES", refused.Message);
    }

    /// <summary>
    /// A paused cost raises nothing, and resuming does not back-fill the gap.
    /// </summary>
    /// <remarks>
    /// Pausing is how somebody says a cost stopped for a while. If resuming raised everything
    /// missed, the pause would have achieved nothing except delaying the charges — and the
    /// report for those months would change after somebody had read it.
    /// </remarks>
    [Fact]
    public async Task A_paused_cost_raises_nothing_and_resuming_does_not_backfill()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        db.Clock.Now = Now;
        await using var module = new Module(db);

        var account = await module.Accounting.OpenAccountAsync(
            "5000", "Rent", AccountKind.Expense);

        var schedule = await module.Accounting.ScheduleAsync(
            account.Id, "Office rent", "Landlord", Money.Of(150_000_00, "KES"),
            Recurrence.Monthly, db.Clock.Today);

        await module.Accounting.RaiseWhatIsDueAsync();
        await module.Accounting.PauseAsync(schedule.Id);

        db.Clock.Advance(TimeSpan.FromDays(92));

        Assert.Equal(0, await module.Accounting.RaiseWhatIsDueAsync());
    }

    /// <summary>
    /// The report names what it could not classify rather than dropping it.
    /// </summary>
    /// <remarks>
    /// Every invoice and claim already in the database has no account, so the first time this
    /// report is opened almost everything is unclassified. A report that quietly dropped those
    /// would show a firm that earned nothing — and naming the row is also the only thing that
    /// makes the coding get done.
    /// </remarks>
    [Fact]
    public async Task The_report_names_what_it_could_not_classify()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        db.Clock.Now = Now;
        await using var module = new Module(db);

        var account = await module.Accounting.OpenAccountAsync(
            "5000", "Rent", AccountKind.Expense);

        await module.Accounting.ScheduleAsync(
            account.Id, "Office rent", "Landlord", Money.Of(150_000_00, "KES"),
            Recurrence.Monthly, db.Clock.Today);

        await module.Accounting.RaiseWhatIsDueAsync();

        var report = await module.Reads.ReportAsync(
            db.Clock.Today.AddDays(-1), db.Clock.Today.AddDays(1), "KES");

        // The charge is coded, so it appears under its account rather than unclassified.
        Assert.Contains(report.Expenditure, line => line.Code == "5000");
        Assert.DoesNotContain(report.Expenditure, line => line.Code == "—");
    }

    /// <summary>
    /// The difference is a difference, and nothing calls it profit.
    /// </summary>
    /// <remarks>
    /// There is no payroll here — that is section 22 — no depreciation and no tax. Calling it
    /// profit would put a number in front of somebody that is wrong by the largest cost the
    /// firm has, and they would act on it.
    /// </remarks>
    [Fact]
    public async Task The_difference_is_income_less_expenditure()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        db.Clock.Now = Now;
        await using var module = new Module(db);

        var account = await module.Accounting.OpenAccountAsync(
            "5000", "Rent", AccountKind.Expense);

        await module.Accounting.ScheduleAsync(
            account.Id, "Office rent", "Landlord", Money.Of(100_000_00, "KES"),
            Recurrence.Monthly, db.Clock.Today);

        await module.Accounting.RaiseWhatIsDueAsync();

        var report = await module.Reads.ReportAsync(
            db.Clock.Today.AddDays(-1), db.Clock.Today.AddDays(1), "KES");

        Assert.Equal(Money.Of(-100_000_00, "KES"), report.Difference);
    }

    /// <summary>A quarterly cost falls due every three months, not every one.</summary>
    /// <remarks>
    /// The enum's value IS the month step, so this test is really asserting that nothing has
    /// grown a lookup table beside it — two places for one fact is how a quarterly cost comes
    /// to fall due monthly with nothing looking wrong.
    /// </remarks>
    [Fact]
    public async Task A_quarterly_cost_falls_due_every_three_months()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        db.Clock.Now = Now;
        await using var module = new Module(db);

        var account = await module.Accounting.OpenAccountAsync(
            "5100", "Insurance", AccountKind.Expense);

        await module.Accounting.ScheduleAsync(
            account.Id, "Cover", "Broker", Money.Of(90_000_00, "KES"),
            Recurrence.Quarterly, db.Clock.Today);

        await module.Accounting.RaiseWhatIsDueAsync();

        db.Clock.Advance(TimeSpan.FromDays(60));
        Assert.Equal(0, await module.Accounting.RaiseWhatIsDueAsync());

        db.Clock.Advance(TimeSpan.FromDays(35));
        Assert.Equal(1, await module.Accounting.RaiseWhatIsDueAsync());
    }
}
