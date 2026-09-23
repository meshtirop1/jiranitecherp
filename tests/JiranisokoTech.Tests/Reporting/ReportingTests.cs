using JiranisokoTech.Application.Business;
using JiranisokoTech.Application.People;
using JiranisokoTech.Application.Settings;
using JiranisokoTech.Application.Work;
using JiranisokoTech.Domain.Money;
using JiranisokoTech.Domain.Time;
using JiranisokoTech.Infrastructure.Business;
using JiranisokoTech.Infrastructure.People;
using JiranisokoTech.Infrastructure.Settings;
using JiranisokoTech.Infrastructure.Reporting;
using JiranisokoTech.Infrastructure.Work;
using JiranisokoTech.Tests.Infrastructure;
using Money = JiranisokoTech.Domain.Common.Money;

namespace JiranisokoTech.Tests.Reporting;

/// <summary>
/// The figures the firm is run on, checked against facts somebody put there.
/// </summary>
/// <remarks>
/// A reporting query is the easiest kind of code to get quietly wrong: it
/// compiles, it returns a number, and the number looks plausible. Nobody
/// notices a total that is missing one category until somebody acts on it. So
/// each test here sets up a known situation and asserts the exact figure, and
/// several of them plant a row that must <em>not</em> be counted — which is
/// where these actually go wrong.
/// </remarks>
public class ReportingTests
{
    private static readonly DateOnly Monday = new(2026, 9, 21);

    private sealed class Module(DatabaseFixture db) : IAsyncDisposable
    {
        private readonly TestDbContext _context = db.NewContext();

        private BusinessRepository Repository => new(_context);

        public PeopleService People => new(new PeopleRepository(_context));

        public WorkService Work =>
            new(new WorkRepository(_context), new PeopleRepository(_context), db.Clock);

        public ClientService Clients => new(Repository);

        public TimesheetService Timesheets =>
            new(Repository, new PeopleRepository(_context), db.Clock);

        public LeaveService Leave => new(Repository, db.Clock);

        public ExpenseService Expenses => new(Repository, db.Clock);

        public SettingsService Settings => new(new SettingsRepository(_context));

        public InvoiceService Invoices => new(Repository, Settings, db.Clock);

        public ReportingQueries Reporting => new(_context);

        public ValueTask DisposeAsync() => _context.DisposeAsync();
    }

    private static async Task<Guid> WorkingAsync(Module module, string name)
    {
        var person = await module.People.HireAsync(name, Monday);
        await module.People.StartAsync(person.Id);

        return person.Id;
    }

    /// <summary>
    /// Unbilled hours are approved, billable, and on no invoice — all three.
    /// </summary>
    /// <remarks>
    /// The figure exists to say how much work the firm has done and not charged
    /// for. Counting unapproved time would overstate it with hours nobody has
    /// agreed; counting non-billable time would overstate it with hours no
    /// client was ever going to pay for; counting billed time would count the
    /// same work twice. So all three are planted here and only one is expected.
    /// </remarks>
    [Fact]
    public async Task Unbilled_hours_are_approved_billable_and_not_yet_invoiced()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var engineer = await WorkingAsync(module, "Duncan");
        var head = await WorkingAsync(module, "Charity Jepchirchir");
        var client = await module.Clients.TakeOnAsync("Acme Logistics");
        var project = await module.Work.BeginProjectAsync("Fleet tracking");

        // Two projects, because billing takes a whole project at a time: every
        // approved billable hour on it goes onto the invoice. Leaving the hour
        // that should stay unbilled on the same project would have it swept up
        // with the rest, which is what this test caught the first time it ran.
        var other = await module.Work.BeginProjectAsync("Depot stock counts");

        // Counts: approved, billable, not invoiced.
        var counted = await module.Timesheets.LogAsync(
            engineer, Monday, 3 * 60, projectId: other.Id);
        await module.Timesheets.ApproveAsync(counted.Id, head);

        // Does not count: never approved.
        await module.Timesheets.LogAsync(engineer, Monday, 60, projectId: other.Id);

        // Does not count: approved but recorded as not billable.
        var free = await module.Timesheets.LogAsync(
            engineer, Monday.AddDays(-3), 2 * 60, projectId: other.Id, billable: false);
        await module.Timesheets.ApproveAsync(free.Id, head);

        // Does not count: approved, billable, and already on an invoice.
        var billed = await module.Timesheets.LogAsync(
            engineer, Monday.AddDays(-4), 4 * 60, projectId: project.Id);
        await module.Timesheets.ApproveAsync(billed.Id, head);

        var invoice = await module.Invoices.DraftAsync(client.Id);
        await module.Invoices.BillTimeAsync(invoice.Id, project.Id, Money.Of(500_00, "KES"));

        var state = await module.Reporting.StateAsync(db.Clock.Today);

        Assert.Equal(3 * 60, state.UnbilledMinutes);
    }

    /// <summary>
    /// A draft invoice is not money anybody owes us.
    /// </summary>
    /// <remarks>
    /// Nobody outside the firm has seen a draft. Counting it as outstanding
    /// reports income that has not been asked for, which is the direction that
    /// gets somebody to stop chasing.
    /// </remarks>
    [Fact]
    public async Task A_draft_invoice_is_not_outstanding()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Logistics");

        var draft = await module.Invoices.DraftAsync(client.Id);
        await module.Invoices.AddLineAsync(draft.Id, "Work", 1, Money.Of(10_000_00, "KES"));

        var sent = await module.Invoices.DraftAsync(client.Id);
        await module.Invoices.AddLineAsync(sent.Id, "Work", 1, Money.Of(4_000_00, "KES"));
        await module.Invoices.SendAsync(sent.Id);

        var state = await module.Reporting.StateAsync(db.Clock.Today);

        Assert.Equal(Money.Of(4_000_00, "KES"), state.Outstanding.Amount);
    }

    /// <summary>
    /// What is outstanding is what is left after payments, not the invoice total.
    /// </summary>
    [Fact]
    public async Task A_part_paid_invoice_counts_only_what_is_left()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Logistics");

        var invoice = await module.Invoices.DraftAsync(client.Id);
        await module.Invoices.AddLineAsync(invoice.Id, "Work", 1, Money.Of(10_000_00, "KES"));
        await module.Invoices.SendAsync(invoice.Id);
        await module.Invoices.RecordPaymentAsync(
            invoice.Id, Money.Of(6_000_00, "KES"), db.Clock.Today, "M-Pesa");

        var state = await module.Reporting.StateAsync(db.Clock.Today);

        Assert.Equal(Money.Of(4_000_00, "KES"), state.Outstanding.Amount);
    }

    /// <summary>
    /// Overdue is judged against the day being asked about, not stored.
    /// </summary>
    /// <remarks>
    /// The same invoice is not overdue on one day and overdue on the next
    /// because anything happened to it — only because the date moved. Asking
    /// the query on two different days is the only way to show that the figure
    /// tracks the calendar rather than a column somebody has to remember to
    /// update.
    /// </remarks>
    [Fact]
    public async Task Overdue_is_worked_out_from_the_day_being_asked_about()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Logistics");

        var invoice = await module.Invoices.DraftAsync(client.Id);
        await module.Invoices.AddLineAsync(invoice.Id, "Work", 1, Money.Of(1_000_00, "KES"));
        await module.Invoices.SendAsync(invoice.Id);

        var due = (await module.Reporting.StateAsync(db.Clock.Today)).Outstanding.Amount;
        Assert.Equal(Money.Of(1_000_00, "KES"), due);

        var today = await module.Reporting.StateAsync(db.Clock.Today);
        Assert.Equal(0, today.OverdueCount);

        // The client's terms are 30 days, so a day past that is overdue and
        // nothing about the invoice has changed.
        var later = await module.Reporting.StateAsync(db.Clock.Today.AddDays(31));

        Assert.Equal(1, later.OverdueCount);
        Assert.Equal(Money.Of(1_000_00, "KES"), later.Overdue.Amount);
    }

    /// <summary>
    /// What is owed to staff is approved and unpaid, not merely claimed.
    /// </summary>
    [Fact]
    public async Task Money_owed_to_staff_is_approved_and_not_yet_paid()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var engineer = await WorkingAsync(module, "Duncan");

        // Counts: approved, unpaid.
        var owed = await module.Expenses.ClaimAsync(
            engineer, Money.Of(3_500_00, "KES"), ExpenseCategory.Travel, Monday, "Matatu to site");
        await module.Expenses.SubmitAsync(owed.Id);
        await module.Expenses.RecordDecisionAsync(owed.Id, true, null);

        // Does not count: still waiting on a decision.
        var waiting = await module.Expenses.ClaimAsync(
            engineer, Money.Of(900_00, "KES"), ExpenseCategory.Meals, Monday, "Lunch");
        await module.Expenses.SubmitAsync(waiting.Id);

        // Does not count: already paid.
        var settled = await module.Expenses.ClaimAsync(
            engineer, Money.Of(1_200_00, "KES"), ExpenseCategory.Travel, Monday, "Fuel");
        await module.Expenses.SubmitAsync(settled.Id);
        await module.Expenses.RecordDecisionAsync(settled.Id, true, null);
        await module.Expenses.PayAsync(settled.Id, "Bank");

        var state = await module.Reporting.StateAsync(db.Clock.Today);

        Assert.Equal(Money.Of(3_500_00, "KES"), state.OwedToStaff.Amount);
        Assert.Equal(1, state.OwedToStaffCount);
        Assert.Equal(1, state.ClaimsWaiting);
    }

    /// <summary>
    /// Leave inside the fortnight is listed; leave beyond it is not.
    /// </summary>
    [Fact]
    public async Task Only_approved_leave_in_the_next_fortnight_is_listed()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var engineer = await WorkingAsync(module, "Duncan");
        var today = db.Clock.Today;

        var soon = await module.Leave.AskForAsync(
            engineer, LeaveKind.Annual, today.AddDays(3), today.AddDays(5), "A break");
        await module.Leave.SubmitAsync(soon.Id);
        await module.Leave.RecordDecisionAsync(soon.Id, true, null);

        // Beyond the fortnight, so not listed — approved all the same.
        var later = await module.Leave.AskForAsync(
            engineer, LeaveKind.Annual, today.AddDays(40), today.AddDays(44), "Christmas");
        await module.Leave.SubmitAsync(later.Id);
        await module.Leave.RecordDecisionAsync(later.Id, true, null);

        // Waiting, so counted as waiting and not listed as away.
        var undecided = await module.Leave.AskForAsync(
            engineer, LeaveKind.Study, today.AddDays(8), today.AddDays(9), "An exam");
        await module.Leave.SubmitAsync(undecided.Id);

        var state = await module.Reporting.StateAsync(today);

        Assert.Single(state.AwaySoon);
        Assert.Equal("Duncan", state.AwaySoon[0].Name);
        Assert.Equal(1, state.LeaveWaiting);
    }

    /// <summary>
    /// Invoices in two currencies report that there is no single figure, not
    /// that everything has been paid.
    /// </summary>
    /// <remarks>
    /// The query used to answer "nothing to total" and "cannot be totalled"
    /// with the same null, so the page rendered "Nothing is outstanding. Every
    /// invoice sent has been paid." for a firm holding unpaid invoices in two
    /// currencies — false, and false in the direction that stops somebody
    /// chasing money. The comment on the helper claimed the page said so
    /// instead of lying; it could not, because it had not been told.
    /// </remarks>
    [Fact]
    public async Task Two_currencies_are_reported_as_untotallable_rather_than_paid()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Logistics");

        await module.Settings.BillAsAsync("JTS", "KES", 30);
        var shillings = await module.Invoices.DraftAsync(client.Id);
        await module.Invoices.AddLineAsync(shillings.Id, "Work", 1, Money.Of(1_000_00, "KES"));
        await module.Invoices.SendAsync(shillings.Id);

        // The firm changing currency is refused once it has invoiced, so the
        // only way a second currency reaches the books is an invoice raised
        // before that rule existed. Written directly for that reason.
        await using (var write = db.NewContext())
        {
            var dollars = Invoice.Draft(client.Id, "USD-0001", "USD", db.Clock.Today, 30);

            dollars.AddLine("Work", 1, Money.Of(500_00, "USD"));
            dollars.Send(db.Clock.Now);

            write.Invoices.Add(dollars);
            await write.SaveChangesAsync();
        }

        var state = await module.Reporting.StateAsync(db.Clock.Today);

        Assert.Null(state.Outstanding.Amount);
        Assert.True(
            state.Outstanding.Mixed,
            "Two currencies were reported as nothing outstanding rather than as untotallable.");
    }

    /// <summary>
    /// An empty firm reports nothing rather than zero.
    /// </summary>
    /// <remarks>
    /// "KES 0.00 outstanding" and "no invoices have been sent" are different
    /// statements, and only one of them is true on a database nobody has used
    /// yet. The page shows a sentence rather than a figure when there is
    /// nothing to total, so the query returns null rather than a zero.
    /// </remarks>
    [Fact]
    public async Task Nothing_at_all_reports_nothing_rather_than_zero()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var state = await module.Reporting.StateAsync(db.Clock.Today);

        Assert.Null(state.Outstanding.Amount);
        Assert.Null(state.OwedToStaff.Amount);
        Assert.Empty(state.AwaySoon);
        Assert.True(state.NothingIsWaiting);
    }
}
