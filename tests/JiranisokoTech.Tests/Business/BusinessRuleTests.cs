using JiranisokoTech.Application.Business;
using JiranisokoTech.Application.People;
using JiranisokoTech.Application.Settings;
using JiranisokoTech.Application.Recruitment;
using JiranisokoTech.Application.Work;
using JiranisokoTech.Domain.Clients;
using JiranisokoTech.Domain.Contracts;
using JiranisokoTech.Domain.Money;
using JiranisokoTech.Domain.Recruitment;
using JiranisokoTech.Domain.Time;
using JiranisokoTech.Infrastructure.Business;
using JiranisokoTech.Infrastructure.People;
using JiranisokoTech.Infrastructure.Settings;
using JiranisokoTech.Infrastructure.Recruitment;
using JiranisokoTech.Infrastructure.Work;
using JiranisokoTech.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Money = JiranisokoTech.Domain.Common.Money;

namespace JiranisokoTech.Tests.Business;

/// <summary>
/// The six business modules, and the rules that cost money when they are
/// missing.
/// </summary>
public class BusinessRuleTests
{
    private static readonly DateOnly Monday = new(2026, 9, 21);

    private sealed class Module(DatabaseFixture db) : IAsyncDisposable
    {
        private readonly TestDbContext _context = db.NewContext();

        private BusinessRepository Repository => new(_context);

        public PeopleService People => new(new PeopleRepository(_context), db.Clock);

        public WorkService Work =>
            new(new WorkRepository(_context), new PeopleRepository(_context), db.Clock);

        public RecruitmentService Recruitment => new(
            new RecruitmentRepository(_context),
            new PeopleRepository(_context),
            new NoCvStore(),
            db.Clock);

        public ClientService Clients => new(Repository);

        public ContractService Contracts => new(Repository, Settings, db.Clock);

        public BusinessQueries Reads => new(_context);

        public TimesheetService Timesheets =>
            new(Repository, new PeopleRepository(_context), db.Clock);

        public LeaveService Leave => new(Repository, db.Clock);

        public HolidayService Holidays => new(Repository);

        public ExpenseService Expenses => new(Repository, db.Clock);

        public SettingsService Settings => new(new SettingsRepository(_context));

        public InvoiceService Invoices => new(Repository, Settings, db.Clock);

        public InterviewService Interviews =>
            new(Repository, new PeopleRepository(_context), db.Clock);

        public ValueTask DisposeAsync() => _context.DisposeAsync();
    }

    /// <summary>These tests never upload anything, so nothing needs storing.</summary>
    private sealed class NoCvStore : ICvStore
    {
        public Task<string> SaveAsync(
            Stream contents, string originalName, CancellationToken cancellationToken = default) =>
            Task.FromResult(originalName);

        public Task<Stream?> OpenAsync(
            string storedName, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream?>(null);

        public Task DeleteAsync(string storedName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private static async Task<Guid> WorkingAsync(Module module, string name)
    {
        var person = await module.People.HireAsync(name, Monday);
        await module.People.StartAsync(person.Id);

        return person.Id;
    }

    // --- clients -----------------------------------------------------------

    /// <summary>
    /// A line added to an invoice that was loaded from the database is actually
    /// saved.
    /// </summary>
    /// <remarks>
    /// This is a regression test, and the regression was invisible. EF's
    /// convention for a Guid primary key is that the store generates it, and it
    /// decides insert-or-update by whether the key is still at its default.
    /// Every id here is a GUIDv7 the entity gives itself, so a new line looked
    /// like an existing row: EF issued an UPDATE, it matched nothing, no error
    /// was raised anywhere, and the line was gone. Nothing short of reading the
    /// invoice back in a second context catches it — which is why this test
    /// does that rather than trusting the object it just changed.
    /// </remarks>
    [Fact]
    public async Task A_line_added_to_a_reloaded_invoice_is_saved()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        Guid invoiceId;

        await using (var module = new Module(db))
        {
            var client = await module.Clients.TakeOnAsync("Acme Logistics");
            var invoice = await module.Invoices.DraftAsync(client.Id);
            invoiceId = invoice.Id;
        }

        // A second context, so the invoice is genuinely read from the database
        // rather than handed back out of the change tracker.
        await using (var module = new Module(db))
        {
            await module.Invoices.AddLineAsync(
                invoiceId, "Fleet tracking, September", 10, Money.Of(250_00, "KES"));
        }

        await using (var read = db.NewContext())
        {
            var saved = await read.Invoices
                .Include(invoice => invoice.Lines)
                .SingleAsync(invoice => invoice.Id == invoiceId);

            Assert.Single(saved.Lines);
            Assert.Equal(Money.Of(2_500_00, "KES"), saved.Total);
        }
    }


    [Fact]
    public async Task A_client_code_is_unique_because_it_goes_on_invoices()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        await module.Clients.TakeOnAsync("Acme Logistics");

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Clients.TakeOnAsync("acme logistics"));

        Assert.Contains("acme-logistics", refused.Message);
    }

    /// <summary>
    /// Marking somebody a former client with work still running is almost
    /// always a mistake, and the one time it is not, the projects should be
    /// closed first.
    /// </summary>
    [Fact]
    public async Task A_client_with_running_projects_cannot_be_archived()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Logistics");
        var project = await module.Work.BeginProjectAsync("Fleet tracking");

        await using (var write = db.NewContext())
        {
            var stored = await write.Projects.SingleAsync(one => one.Id == project.Id);
            stored.ForClient(client.Id);
            await write.SaveChangesAsync();
        }

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Clients.MoveToAsync(client.Id, ClientStatus.Former));

        Assert.Contains("1 project(s) running", refused.Message);
    }

    // --- contracts ---------------------------------------------------------

    /// <summary>
    /// The reference is what both sides quote at each other, so two contracts
    /// cannot share one.
    /// </summary>
    [Fact]
    public async Task A_contract_reference_is_unique_because_both_sides_quote_it()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Logistics");
        var other = await module.Clients.TakeOnAsync("Bidii Freight");

        await module.Contracts.DraftAsync(client.Id, "JTS-C-2026-001", "Fleet tracking");

        // Refused across clients, not merely within one: the reference goes on
        // correspondence, and a second client quoting the same one has no way to
        // be told apart from the first.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Contracts.DraftAsync(other.Id, "JTS-C-2026-001", "Depot survey"));

        Assert.Contains("JTS-C-2026-001", refused.Message);
    }

    /// <summary>
    /// Agreeing new terms with somebody the firm has stopped working for is
    /// either a mistake or a decision to take them back on, and that is the
    /// conversation this refusal starts.
    /// </summary>
    [Fact]
    public async Task A_former_client_cannot_be_given_new_terms()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Logistics");
        await module.Clients.MoveToAsync(client.Id, ClientStatus.Former);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Contracts.DraftAsync(client.Id, "JTS-C-2026-002", "Fleet tracking"));

        Assert.Contains("former client", refused.Message);
    }

    /// <summary>
    /// A contract cannot come into force without a figure and a span.
    /// </summary>
    /// <remarks>
    /// Read back through a second context rather than asserted on the object that
    /// was just changed, so what is checked is what reached the database.
    /// </remarks>
    [Fact]
    public async Task A_contract_cannot_come_into_force_until_the_terms_are_agreed()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Logistics");
        var contract = await module.Contracts.DraftAsync(
            client.Id, "JTS-C-2026-003", "Fleet tracking");

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Contracts.ActivateAsync(contract.Id));

        Assert.Contains("no value", refused.Message);

        await module.Contracts.AgreeAsync(
            contract.Id,
            "Fleet tracking, year one",
            Money.Of(1_200_000_00, "KES"),
            Monday,
            Monday.AddYears(1));

        await module.Contracts.ActivateAsync(contract.Id);

        await using var read = db.NewContext();
        var stored = await read.Contracts.SingleAsync(one => one.Id == contract.Id);

        Assert.Equal(ContractState.Active, stored.State);
        Assert.Equal(Money.Of(1_200_000_00, "KES"), stored.Value);
        Assert.True(stored.CoversOn(Monday));
    }

    /// <summary>
    /// An end date before the start is refused, and nothing is saved.
    /// </summary>
    [Fact]
    public async Task Terms_that_end_before_they_start_are_refused()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Logistics");
        var contract = await module.Contracts.DraftAsync(
            client.Id, "JTS-C-2026-004", "Depot survey");

        await Assert.ThrowsAsync<ArgumentException>(
            () => module.Contracts.AgreeAsync(
                contract.Id,
                "Depot survey",
                Money.Of(300_000_00, "KES"),
                Monday,
                Monday.AddDays(-1)));

        await using var read = db.NewContext();
        var stored = await read.Contracts.SingleAsync(one => one.Id == contract.Id);

        // Not even the value, which was valid: the whole call is one act.
        Assert.Null(stored.StartsOn);
        Assert.Equal(ContractState.Draft, stored.State);
    }

    /// <summary>
    /// A contract past its end date still reads as active in the database and as
    /// expired to anybody who asks.
    /// </summary>
    /// <remarks>
    /// The rule this module is most likely to get wrong, so it is asserted
    /// against stored rows rather than objects in memory. There is no Expired
    /// state to set, deliberately — one would need a nightly job to become true
    /// and would be wrong for everybody who looked before it ran. The second
    /// contract runs to next year and must not be counted: a check that called
    /// every active contract expired would pass without it.
    /// </remarks>
    [Fact]
    public async Task An_expired_contract_is_still_recorded_active_and_still_reads_as_expired()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Logistics");
        var today = db.Clock.Today;

        var over = await module.Contracts.DraftAsync(client.Id, "JTS-C-2026-005", "Last year");
        await module.Contracts.AgreeAsync(
            over.Id, "Last year", Money.Of(500_000_00, "KES"),
            today.AddMonths(-13), today.AddDays(-1));
        await module.Contracts.ActivateAsync(over.Id);

        var running = await module.Contracts.DraftAsync(client.Id, "JTS-C-2026-006", "This year");
        await module.Contracts.AgreeAsync(
            running.Id, "This year", Money.Of(800_000_00, "KES"), today, today.AddYears(1));
        await module.Contracts.ActivateAsync(running.Id);

        var rows = await module.Reads.ContractsAsync(clientId: client.Id);

        Assert.Equal(2, rows.Count);

        var expired = rows.Single(row => row.Reference == "JTS-C-2026-005");
        var covers = rows.Single(row => row.Reference == "JTS-C-2026-006");

        Assert.Equal(ContractState.Active, expired.State);
        Assert.True(expired.HasExpiredOn(today));
        Assert.False(expired.CoversOn(today));

        // The one that must not be counted.
        Assert.False(covers.HasExpiredOn(today));
        Assert.True(covers.CoversOn(today));

        // And the same two answers off the aggregate, which is the definition the
        // row above is only repeating.
        await using var read = db.NewContext();
        var stored = await read.Contracts.SingleAsync(one => one.Id == over.Id);

        Assert.Equal(ContractState.Active, stored.State);
        Assert.True(stored.HasExpiredOn(today));
    }

    /// <summary>
    /// Asking for one client's contracts returns that client's contracts.
    /// </summary>
    /// <remarks>
    /// The contract planted on the second client is the point of the test. A
    /// query that forgot its filter would return both and every assertion about
    /// the first client would still pass.
    /// </remarks>
    [Fact]
    public async Task Only_the_contracts_of_the_client_asked_about_are_listed()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var ours = await module.Clients.TakeOnAsync("Acme Logistics");
        var theirs = await module.Clients.TakeOnAsync("Bidii Freight");

        await module.Contracts.DraftAsync(ours.Id, "JTS-C-2026-007", "Fleet tracking");
        await module.Contracts.DraftAsync(theirs.Id, "JTS-C-2026-008", "Somebody else's work");

        var rows = await module.Reads.ContractsAsync(clientId: ours.Id);

        Assert.Equal("JTS-C-2026-007", Assert.Single(rows).Reference);
        Assert.Equal("Acme Logistics", rows[0].ClientName);

        // And a draft is not reported as in force, however complete it looks.
        Assert.Equal(ContractState.Draft, rows[0].State);
        Assert.False(rows[0].CoversOn(db.Clock.Today));
    }

    /// <summary>
    /// An invoice can still be raised for a client with no contract at all.
    /// </summary>
    /// <remarks>
    /// Pinning a decision rather than describing a rule, so that reversing it is
    /// a deliberate act with a failing test attached. Requiring a contract would
    /// refuse every invoice for every client taken on before contracts existed,
    /// including for work already delivered — and a system that will not invoice
    /// does not make a firm careful, it stops the firm being paid. The gap is
    /// surfaced on the client page instead.
    /// </remarks>
    [Fact]
    public async Task An_invoice_can_still_be_raised_for_a_client_with_no_contract()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Logistics");

        Assert.Empty(await module.Reads.ContractsAsync(clientId: client.Id));

        var invoice = await module.Invoices.DraftAsync(client.Id);

        await module.Invoices.AddLineAsync(
            invoice.Id, "Delivery work", 1, Money.Of(10_000_00, "KES"));

        await using var read = db.NewContext();
        var stored = await read.Invoices
            .Include(one => one.Lines)
            .SingleAsync(one => one.Id == invoice.Id);

        Assert.Equal(Money.Of(10_000_00, "KES"), stored.Total);
    }

    /// <summary>
    /// An extension is saved against the contract that was read back from the
    /// database rather than the one still in memory.
    /// </summary>
    [Fact]
    public async Task An_extension_reaches_the_database_and_a_shortening_does_not()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Logistics");
        var today = db.Clock.Today;

        var contract = await module.Contracts.DraftAsync(
            client.Id, "JTS-C-2026-009", "Fleet tracking");

        await module.Contracts.AgreeAsync(
            contract.Id, "Fleet tracking", Money.Of(1_000_000_00, "KES"),
            today, today.AddMonths(6));
        await module.Contracts.ActivateAsync(contract.Id);

        await Assert.ThrowsAsync<ArgumentException>(
            () => module.Contracts.ExtendAsync(contract.Id, today.AddMonths(3)));

        await module.Contracts.ExtendAsync(contract.Id, today.AddMonths(12));

        await using var read = db.NewContext();
        var stored = await read.Contracts.SingleAsync(one => one.Id == contract.Id);

        Assert.Equal(today.AddMonths(12), stored.EndsOn);
    }

    /// <summary>
    /// Terminating records why, and the contract stops covering anything.
    /// </summary>
    [Fact]
    public async Task A_terminated_contract_keeps_its_reason_and_covers_nothing()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Logistics");
        var today = db.Clock.Today;

        var contract = await module.Contracts.DraftAsync(
            client.Id, "JTS-C-2026-010", "Fleet tracking");

        await module.Contracts.AgreeAsync(
            contract.Id, "Fleet tracking", Money.Of(1_000_000_00, "KES"),
            today, today.AddYears(1));
        await module.Contracts.ActivateAsync(contract.Id);

        await Assert.ThrowsAsync<ArgumentException>(
            () => module.Contracts.TerminateAsync(contract.Id, "  "));

        await module.Contracts.TerminateAsync(contract.Id, "They took delivery in house.");

        await using var read = db.NewContext();
        var stored = await read.Contracts.SingleAsync(one => one.Id == contract.Id);

        Assert.Equal(ContractState.Terminated, stored.State);
        Assert.Equal("They took delivery in house.", stored.Outcome);
        Assert.False(stored.CoversOn(today));
        Assert.False(stored.HasExpiredOn(today.AddYears(2)));
    }

    // --- timesheets --------------------------------------------------------

    /// <summary>
    /// A day is only so long. A timesheet that says otherwise is a typo or the
    /// same work logged twice, and both reach a client invoice if nothing stops
    /// them here.
    /// </summary>
    [Fact]
    public async Task A_day_cannot_hold_more_hours_than_it_has()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var engineer = await WorkingAsync(module, "Duncan");

        await module.Timesheets.LogAsync(engineer, Monday, 8 * 60);
        await module.Timesheets.LogAsync(engineer, Monday, 7 * 60);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Timesheets.LogAsync(engineer, Monday, 3 * 60));

        Assert.Contains("past 16 hours", refused.Message);
    }

    [Fact]
    public async Task Time_cannot_be_logged_for_a_day_that_has_not_happened()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var engineer = await WorkingAsync(module, "Duncan");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Timesheets.LogAsync(engineer, db.Clock.Today.AddDays(1), 60));
    }

    /// <summary>
    /// Not a rule about trust. A timesheet somebody approves for themselves is
    /// not an approval, and every audit of one says so.
    /// </summary>
    [Fact]
    public async Task Nobody_approves_their_own_time()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var engineer = await WorkingAsync(module, "Duncan");
        var entry = await module.Timesheets.LogAsync(engineer, Monday, 60);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Timesheets.ApproveAsync(entry.Id, engineer));
    }

    [Fact]
    public async Task Approved_time_cannot_be_quietly_edited()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var engineer = await WorkingAsync(module, "Duncan");
        var head = await WorkingAsync(module, "Charity Jepchirchir");

        var entry = await module.Timesheets.LogAsync(engineer, Monday, 60);

        await module.Timesheets.ApproveAsync(entry.Id, head);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Timesheets.AmendAsync(entry.Id, 480, "actually all day", true));

        Assert.Contains("has been approved", refused.Message);
    }

    // --- leave -------------------------------------------------------------

    /// <summary>
    /// Two overlapping requests are how somebody comes to be marked away twice
    /// for the same week, and charged twice for it.
    /// </summary>
    [Fact]
    public async Task Leave_cannot_overlap_leave_already_asked_for()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var engineer = await WorkingAsync(module, "Purity");
        var from = db.Clock.Today.AddDays(10);

        await module.Leave.AskForAsync(
            engineer, LeaveKind.Annual, from, from.AddDays(4), "A week off.");

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Leave.AskForAsync(
                engineer, LeaveKind.Annual, from.AddDays(2), from.AddDays(6), "Another week."));

        Assert.Contains("overlaps leave already asked for", refused.Message);
    }

    /// <summary>Nobody schedules being ill.</summary>
    [Fact]
    public async Task Sick_leave_can_be_recorded_afterwards_and_annual_leave_cannot()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var engineer = await WorkingAsync(module, "Faith");

        // Last Thursday rather than yesterday, and the distinction is new: the
        // clock sits on a Monday, so yesterday is a Sunday, and a request with no
        // working time in it is now refused outright. That refusal is the subject
        // of a test of its own; this one is about being allowed to date sick leave
        // in the past at all, so it uses a day somebody was actually working.
        var lastThursday = db.Clock.Today.AddDays(-4);

        var sick = await module.Leave.AskForAsync(
            engineer, LeaveKind.Sick, lastThursday, lastThursday, "Food poisoning.");

        Assert.Equal(LeaveStatus.Draft, sick.Status);
        Assert.Equal(1, sick.Days);

        // A different day, so this is refused for being in the past rather
        // than for overlapping the sick day above.
        var lastWeek = db.Clock.Today.AddDays(-7);

        await Assert.ThrowsAsync<ArgumentException>(
            () => module.Leave.AskForAsync(
                engineer, LeaveKind.Annual, lastWeek, lastWeek, "A day off."));
    }

    [Fact]
    public async Task Leave_counts_working_days_and_skips_the_weekend()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var engineer = await WorkingAsync(module, "Brian");

        // Monday to the following Friday: ten working days, fourteen calendar.
        var from = new DateOnly(2026, 10, 5);

        var leave = await module.Leave.AskForAsync(
            engineer, LeaveKind.Annual, from, from.AddDays(13), "Two weeks.");

        Assert.Equal(10, leave.Days);
    }

    // --- public holidays ----------------------------------------------------

    /// <summary>Monday 21 December 2026, the Monday of Christmas week.</summary>
    private static readonly DateOnly ChristmasWeek = new(2026, 12, 21);

    /// <summary>Christmas Day 2026, which is a Friday.</summary>
    private static readonly DateOnly ChristmasDay = new(2026, 12, 25);

    /// <summary>Boxing Day 2026, which is a Saturday.</summary>
    private static readonly DateOnly BoxingDay = new(2026, 12, 26);

    /// <summary>
    /// The reason this feature exists. A week off over Christmas is four days of
    /// somebody's entitlement, not five.
    /// </summary>
    /// <remarks>
    /// In Kenya this is roughly a dozen days a year for every member of staff.
    /// Charging them as ordinary leave is not a rounding error; it is a fortnight
    /// of entitlement taken from everybody in the firm over two years.
    /// </remarks>
    [Fact]
    public async Task Leave_across_a_public_holiday_is_charged_a_day_less()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var engineer = await WorkingAsync(module, "Kevin");

        await module.Holidays.DeclareAsync(ChristmasDay, "Christmas Day");

        var leave = await module.Leave.AskForAsync(
            engineer, LeaveKind.Annual, ChristmasWeek, ChristmasWeek.AddDays(4), "Christmas.");

        Assert.Equal(4, leave.Days);
    }

    /// <summary>
    /// A public holiday on a weekend gives nothing back.
    /// </summary>
    /// <remarks>
    /// The mistake this rules out is subtracting the holidays in a range from the
    /// weekdays in it, which deducts a Saturday nobody was being charged for.
    /// Boxing Day 2026 falls on a Saturday, and Kenya has one of these most
    /// years, so the wrong version would be handing out invented leave regularly.
    /// </remarks>
    [Fact]
    public async Task A_public_holiday_on_a_weekend_is_not_deducted_twice()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var engineer = await WorkingAsync(module, "Mercy");

        await module.Holidays.DeclareAsync(BoxingDay, "Boxing Day");

        // Monday the 21st to the following Monday the 28th: six working days,
        // with the Saturday holiday sitting in the middle of them.
        var leave = await module.Leave.AskForAsync(
            engineer, LeaveKind.Annual, ChristmasWeek, ChristmasWeek.AddDays(7), "Christmas.");

        Assert.Equal(6, leave.Days);
    }

    /// <summary>
    /// Leave with no working time in it is refused rather than recorded as
    /// nought.
    /// </summary>
    /// <remarks>
    /// Refused because a zero-day request is not harmless. It asks somebody to
    /// approve nothing, and the overlap rule then refuses any real request
    /// touching those dates — so a request that costs nobody anything would sit
    /// there blocking the one that matters. The useful answer is that those days
    /// are already off, which is what the refusal says.
    /// </remarks>
    [Fact]
    public async Task Leave_entirely_inside_holidays_and_weekends_is_refused()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var engineer = await WorkingAsync(module, "Dennis");

        await module.Holidays.DeclareAsync(ChristmasDay, "Christmas Day");

        // Friday the 25th to Sunday the 27th: a holiday and a weekend.
        var refused = await Assert.ThrowsAsync<ArgumentException>(
            () => module.Leave.AskForAsync(
                engineer,
                LeaveKind.Annual,
                ChristmasDay,
                ChristmasDay.AddDays(2),
                "The Christmas weekend."));

        Assert.Contains("no working time in it", refused.Message);
    }

    /// <summary>
    /// A holiday declared after leave was approved shortens it.
    /// </summary>
    /// <remarks>
    /// The deliberate answer to the awkward question, and the reason for it is
    /// the Kenyan practice of gazetting a public holiday with a week's notice or
    /// less. By the time the notice appears, leave over those dates is long
    /// approved; freezing the figure means the firm charges people for a day the
    /// country was shut every single time it happens, and the only people who get
    /// it back are the ones who notice and ask.
    ///
    /// Read back through a second context, because what is being asserted is that
    /// the recount reached the database rather than that an object in memory was
    /// changed.
    /// </remarks>
    [Fact]
    public async Task Declaring_a_holiday_recounts_leave_that_is_already_approved()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        Guid leaveId;

        await using (var module = new Module(db))
        {
            var engineer = await WorkingAsync(module, "Sharon");

            var leave = await module.Leave.AskForAsync(
                engineer, LeaveKind.Annual, ChristmasWeek, ChristmasWeek.AddDays(4), "Christmas.");

            leaveId = leave.Id;

            Assert.Equal(5, leave.Days);

            await module.Leave.SubmitAsync(leaveId);
            await module.Leave.RecordDecisionAsync(leaveId, approved: true, reason: null);
        }

        await using (var module = new Module(db))
        {
            var recounted = await module.Holidays.DeclareAsync(ChristmasDay, "Christmas Day");

            Assert.Equal(1, recounted);
        }

        await using (var read = db.NewContext())
        {
            var saved = await read.Leave.SingleAsync(leave => leave.Id == leaveId);

            Assert.Equal(4, saved.Days);
            Assert.Equal(LeaveStatus.Approved, saved.Status);
        }
    }

    /// <summary>
    /// Withdrawing a holiday puts the day back.
    /// </summary>
    /// <remarks>
    /// A day typed in by mistake has to be reversible in both directions. Taking
    /// the row out and leaving the shortened counts alone is the error that
    /// favours the employee and would therefore go unreported for years.
    /// </remarks>
    [Fact]
    public async Task Withdrawing_a_holiday_puts_the_day_back_onto_the_leave_it_shortened()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        Guid leaveId;
        Guid holidayId;

        await using (var module = new Module(db))
        {
            var engineer = await WorkingAsync(module, "Collins");

            var leave = await module.Leave.AskForAsync(
                engineer, LeaveKind.Annual, ChristmasWeek, ChristmasWeek.AddDays(4), "Christmas.");

            leaveId = leave.Id;

            await module.Holidays.DeclareAsync(ChristmasDay, "Christmas Day");
        }

        await using (var read = db.NewContext())
        {
            Assert.Equal(4, (await read.Leave.SingleAsync(leave => leave.Id == leaveId)).Days);

            holidayId = (await read.Holidays.SingleAsync(holiday => holiday.On == ChristmasDay)).Id;
        }

        await using (var module = new Module(db))
        {
            Assert.Equal(1, await module.Holidays.WithdrawAsync(holidayId));
        }

        await using (var read = db.NewContext())
        {
            Assert.Equal(5, (await read.Leave.SingleAsync(leave => leave.Id == leaveId)).Days);
            Assert.Empty(read.Holidays);
        }
    }

    /// <summary>
    /// Leave that was cancelled is left where it is.
    /// </summary>
    /// <remarks>
    /// It charges nobody anything, so recounting it moves no entitlement and
    /// would put an audit entry against it for every holiday ever declared across
    /// those dates — noise in the one record that has to stay readable.
    /// </remarks>
    [Fact]
    public async Task Declaring_a_holiday_leaves_cancelled_leave_alone()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        Guid leaveId;

        await using (var module = new Module(db))
        {
            var engineer = await WorkingAsync(module, "Naomi");

            var leave = await module.Leave.AskForAsync(
                engineer, LeaveKind.Annual, ChristmasWeek, ChristmasWeek.AddDays(4), "Christmas.");

            leaveId = leave.Id;

            await module.Leave.CancelAsync(leaveId);
        }

        await using (var module = new Module(db))
        {
            Assert.Equal(0, await module.Holidays.DeclareAsync(ChristmasDay, "Christmas Day"));
        }

        await using (var read = db.NewContext())
        {
            var saved = await read.Leave.SingleAsync(leave => leave.Id == leaveId);

            Assert.Equal(5, saved.Days);
            Assert.Equal(LeaveStatus.Cancelled, saved.Status);
        }
    }

    /// <summary>
    /// One date, one row.
    /// </summary>
    /// <remarks>
    /// A duplicate would not change any day count — the calendar is read as a set
    /// of dates — but it makes the screen misreport the calendar, and withdrawing
    /// the day then silently only half works.
    /// </remarks>
    [Fact]
    public async Task A_date_cannot_be_declared_a_holiday_twice()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        await module.Holidays.DeclareAsync(ChristmasDay, "Christmas Day");

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Holidays.DeclareAsync(ChristmasDay, "Christmas"));

        Assert.Contains("already on the calendar", refused.Message);
    }

    /// <summary>
    /// The list a screen reads and the figure the aggregate agreed are the same
    /// number.
    /// </summary>
    /// <remarks>
    /// This is the test that would have caught the drift the old arrangement
    /// invited. The leave list used to hold its own copy of the weekend rule so
    /// that thirty rows need not become thirty aggregates; that copy knew nothing
    /// about holidays, and the day it and the aggregate disagreed, somebody's
    /// leave balance would have depended on which screen was open. The list now
    /// selects the stored column, and this asserts the two agree over a holiday.
    /// </remarks>
    [Fact]
    public async Task The_leave_list_reports_the_same_count_the_request_agreed()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var engineer = await WorkingAsync(module, "Wanjiru");

        await module.Holidays.DeclareAsync(ChristmasDay, "Christmas Day");

        var leave = await module.Leave.AskForAsync(
            engineer, LeaveKind.Annual, ChristmasWeek, ChristmasWeek.AddDays(4), "Christmas.");

        await using var read = db.NewContext();

        var listed = Assert.Single(await new BusinessQueries(read).LeaveAsync(employeeId: engineer));

        Assert.Equal(leave.Days, listed.Days);
        Assert.Equal(4, listed.Days);
    }

    /// <summary>
    /// The calendar is listed by year, because that is how somebody checks it
    /// against a gazette notice.
    /// </summary>
    [Fact]
    public async Task The_calendar_is_read_a_year_at_a_time()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        await module.Holidays.DeclareAsync(ChristmasDay, "Christmas Day");
        await module.Holidays.DeclareAsync(BoxingDay, "Boxing Day");
        await module.Holidays.DeclareAsync(new DateOnly(2027, 1, 1), "New Year's Day");

        await using var read = db.NewContext();
        var queries = new BusinessQueries(read);

        var christmas = await queries.HolidaysAsync(2026);

        Assert.Equal(2, christmas.Count);
        Assert.Equal(ChristmasDay, christmas[0].On);
        Assert.Equal("Boxing Day", christmas[1].Name);

        Assert.Single(await queries.HolidaysAsync(2027));
        Assert.Equal([2027, 2026], await queries.HolidayYearsAsync());
    }

    // --- expenses ----------------------------------------------------------

    /// <summary>
    /// Paying a claim nobody agreed to is the thing this state machine exists
    /// to make impossible, and the one a finance system is audited on.
    /// </summary>
    [Fact]
    public async Task A_claim_cannot_be_paid_before_it_is_approved()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var engineer = await WorkingAsync(module, "Precious");

        var claim = await module.Expenses.ClaimAsync(
            engineer, Money.Of(450_00, "KES"), ExpenseCategory.Travel,
            db.Clock.Today, "Matatu to the Westlands site.");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Expenses.PayAsync(claim.Id, "REF1"));

        await module.Expenses.SubmitAsync(claim.Id);
        await module.Expenses.RecordDecisionAsync(claim.Id, true, null);
        await module.Expenses.PayAsync(claim.Id, "REF1");

        await using var read = db.NewContext();
        var stored = await read.Expenses.SingleAsync(one => one.Id == claim.Id);

        Assert.Equal(ClaimStatus.Paid, stored.Status);
        Assert.False(stored.IsOwed);
    }

    [Fact]
    public async Task A_claim_has_to_be_for_something_and_cannot_be_in_the_future()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var engineer = await WorkingAsync(module, "Precious");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => module.Expenses.ClaimAsync(
                engineer, Money.Of(0, "KES"), ExpenseCategory.Other, db.Clock.Today, "Nothing."));

        await Assert.ThrowsAsync<ArgumentException>(
            () => module.Expenses.ClaimAsync(
                engineer, Money.Of(100_00, "KES"), ExpenseCategory.Other,
                db.Clock.Today.AddDays(1), "Not yet spent."));
    }

    // --- invoices ----------------------------------------------------------

    [Fact]
    public async Task An_invoice_is_numbered_in_sequence_and_totals_its_lines()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Logistics");

        var first = await module.Invoices.DraftAsync(client.Id);
        var second = await module.Invoices.DraftAsync(client.Id);

        Assert.EndsWith("0001", first.Number);
        Assert.EndsWith("0002", second.Number);

        await module.Invoices.AddLineAsync(first.Id, "Delivery work", 3, Money.Of(15_000_00, "KES"));

        await using var read = db.NewContext();
        var stored = await read.Invoices
            .Include(one => one.Lines)
            .SingleAsync(one => one.Id == first.Id);

        Assert.Equal(Money.Of(45_000_00, "KES"), stored.Total);
    }

    /// <summary>
    /// Sending a client a bill for nothing is a conversation nobody wants to
    /// have, and it is always a mistake.
    /// </summary>
    [Fact]
    public async Task An_empty_invoice_cannot_be_sent()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Logistics");
        var invoice = await module.Invoices.DraftAsync(client.Id);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Invoices.SendAsync(invoice.Id));

        Assert.Contains("nothing on this invoice", refused.Message);
    }

    /// <summary>
    /// Somebody outside this firm is holding a document with these figures on
    /// it. Editing our copy leaves the two disagreeing with nothing to say
    /// which is right.
    /// </summary>
    [Fact]
    public async Task A_sent_invoice_cannot_be_changed()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Logistics");
        var invoice = await module.Invoices.DraftAsync(client.Id);

        await module.Invoices.AddLineAsync(invoice.Id, "Delivery work", 1, Money.Of(10_000_00, "KES"));
        await module.Invoices.SendAsync(invoice.Id);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Invoices.AddLineAsync(
                invoice.Id, "One more thing", 1, Money.Of(5_000_00, "KES")));

        Assert.Contains("has been sent and cannot be changed", refused.Message);
    }

    /// <summary>
    /// A client paying more than the bill is a mistake somewhere. Absorbing it
    /// loses the difference, which somebody then finds months later with
    /// nothing to explain it.
    /// </summary>
    [Fact]
    public async Task A_payment_larger_than_the_bill_is_refused()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Logistics");
        var invoice = await module.Invoices.DraftAsync(client.Id);

        await module.Invoices.AddLineAsync(invoice.Id, "Delivery work", 1, Money.Of(10_000_00, "KES"));
        await module.Invoices.SendAsync(invoice.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Invoices.RecordPaymentAsync(
                invoice.Id, Money.Of(12_000_00, "KES"), db.Clock.Today, "REF"));

        // Part payment is fine, and leaves it partly paid rather than paid.
        await module.Invoices.RecordPaymentAsync(
            invoice.Id, Money.Of(4_000_00, "KES"), db.Clock.Today, "REF1");

        await using var read = db.NewContext();
        var stored = await read.Invoices
            .Include(one => one.Lines)
            .Include(one => one.Payments)
            .SingleAsync(one => one.Id == invoice.Id);

        Assert.Equal(InvoiceStatus.PartlyPaid, stored.Status);
        Assert.Equal(Money.Of(6_000_00, "KES"), stored.Outstanding);
    }

    /// <summary>
    /// The point of having timesheets. Each hour is marked with the invoice it
    /// went on, so the same hour cannot be billed twice — the mistake that
    /// costs a client relationship rather than an afternoon.
    /// </summary>
    [Fact]
    public async Task Approved_hours_become_an_invoice_line_and_cannot_be_billed_twice()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Logistics");
        var engineer = await WorkingAsync(module, "Duncan");
        var head = await WorkingAsync(module, "Charity Jepchirchir");

        var project = await module.Work.BeginProjectAsync("Fleet tracking");

        var first = await module.Timesheets.LogAsync(
            engineer, Monday, 4 * 60, projectId: project.Id);
        // The Friday before. Monday is today on the fixture clock, so a second
        // day has to be an earlier one.
        var second = await module.Timesheets.LogAsync(
            engineer, Monday.AddDays(-3), 2 * 60, projectId: project.Id);

        await module.Timesheets.ApproveAsync(first.Id, head);
        await module.Timesheets.ApproveAsync(second.Id, head);

        var invoice = await module.Invoices.DraftAsync(client.Id);

        var billed = await module.Invoices.BillTimeAsync(
            invoice.Id, project.Id, Money.Of(2_500_00, "KES"));

        Assert.Equal(2, billed);

        await using (var read = db.NewContext())
        {
            var stored = await read.Invoices
                .Include(one => one.Lines)
                .SingleAsync(one => one.Id == invoice.Id);

            // Six hours at 2,500 a hour.
            Assert.Equal(Money.Of(15_000_00, "KES"), stored.Total);
        }

        // And there is nothing left to bill.
        var again = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Invoices.BillTimeAsync(
                invoice.Id, project.Id, Money.Of(2_500_00, "KES")));

        Assert.Contains("no approved, billable, uninvoiced hours", again.Message);
    }

    [Fact]
    public async Task Unapproved_hours_are_not_billed()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Logistics");
        var engineer = await WorkingAsync(module, "Duncan");
        var project = await module.Work.BeginProjectAsync("Fleet tracking");

        await module.Timesheets.LogAsync(engineer, Monday, 4 * 60, projectId: project.Id);

        var invoice = await module.Invoices.DraftAsync(client.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Invoices.BillTimeAsync(
                invoice.Id, project.Id, Money.Of(2_500_00, "KES")));
    }

    // --- interviews --------------------------------------------------------

    private async Task<Guid> ApplicationAsync(Module module, DatabaseFixture db)
    {
        var raiser = await WorkingAsync(module, $"Raiser {Guid.NewGuid():N}"[..20]);

        var requisition = await module.Recruitment.RaiseRequisitionAsync(
            "Delivery Engineer", null, 1, "We need somebody.", raiser);

        await module.Recruitment.SubmitAsync(requisition.Id);
        await module.Recruitment.RecordDecisionAsync(requisition.Id, true, null);

        var posting = await module.Recruitment.DraftPostingAsync(
            requisition.Id, "Delivery Engineer", "Build things", "A longer description.",
            slug: $"advert-{Guid.NewGuid():N}"[..20]);

        await module.Recruitment.PublishAsync(posting.Id);

        var application = await module.Recruitment.ApplyAsync(
            posting.Id, "Amina Hassan", $"{Guid.NewGuid():N}@example.com");

        return application.Id;
    }

    /// <summary>
    /// A scorecard filed before the conversation is an opinion formed from a
    /// CV, and one from somebody who was not there is hearsay. Both look the
    /// same on the page as a real one.
    /// </summary>
    [Fact]
    public async Task An_interview_cannot_be_scored_before_it_happens_or_by_a_bystander()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var application = await ApplicationAsync(module, db);
        var interviewer = await WorkingAsync(module, "Tirop Meshack");
        var bystander = await WorkingAsync(module, "Mercy");

        var interview = await module.Interviews.ScheduleAsync(
            application, InterviewKind.Technical, db.Clock.Now.AddDays(2), [interviewer]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Interviews.ScoreAsync(
                interview.Id, interviewer, Recommendation.Yes, "Strong on the database side."));

        await module.Interviews.HeldAsync(interview.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Interviews.ScoreAsync(
                interview.Id, bystander, Recommendation.Yes, "Heard it went well."));

        await module.Interviews.ScoreAsync(
            interview.Id, interviewer, Recommendation.Yes, "Strong on the database side.");

        await using var read = db.NewContext();
        var stored = await read.Interviews
            .Include(one => one.Panel)
            .Include(one => one.Scorecards)
            .SingleAsync(one => one.Id == interview.Id);

        Assert.True(stored.IsScored);
        Assert.Equal(Recommendation.Yes, stored.Verdict());
    }

    /// <summary>
    /// Panels exist so that one person who saw something serious can stop a
    /// hire. Averaging that away is how a firm hires somebody three of four
    /// people had doubts about.
    /// </summary>
    [Fact]
    public async Task One_strong_no_carries_the_panel()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var application = await ApplicationAsync(module, db);
        var first = await WorkingAsync(module, "Tirop Meshack");
        var second = await WorkingAsync(module, "Mercy");
        var third = await WorkingAsync(module, "Brian");

        var interview = await module.Interviews.ScheduleAsync(
            application, InterviewKind.Final, db.Clock.Now, [first, second, third]);

        await module.Interviews.HeldAsync(interview.Id);

        await module.Interviews.ScoreAsync(
            interview.Id, first, Recommendation.StrongYes, "Excellent.");
        await module.Interviews.ScoreAsync(
            interview.Id, second, Recommendation.StrongYes, "Also excellent.");
        await module.Interviews.ScoreAsync(
            interview.Id, third, Recommendation.StrongNo, "Would not work with them again.");

        await using var read = db.NewContext();
        var stored = await read.Interviews
            .Include(one => one.Panel)
            .Include(one => one.Scorecards)
            .SingleAsync(one => one.Id == interview.Id);

        Assert.Equal(Recommendation.StrongNo, stored.Verdict());
    }

    [Fact]
    public async Task Nobody_scores_the_same_interview_twice()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var application = await ApplicationAsync(module, db);
        var interviewer = await WorkingAsync(module, "Tirop Meshack");

        var interview = await module.Interviews.ScheduleAsync(
            application, InterviewKind.Screening, db.Clock.Now, [interviewer]);

        await module.Interviews.HeldAsync(interview.Id);
        await module.Interviews.ScoreAsync(
            interview.Id, interviewer, Recommendation.Yes, "Worth a technical.");

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Interviews.ScoreAsync(
                interview.Id, interviewer, Recommendation.No, "Changed my mind."));

        Assert.Contains("already scored", refused.Message);
    }

    /// <summary>
    /// Interviewing somebody who is out of the process wastes their afternoon
    /// as well as ours.
    /// </summary>
    [Fact]
    public async Task An_interview_cannot_be_booked_against_a_closed_application()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var application = await ApplicationAsync(module, db);
        var interviewer = await WorkingAsync(module, "Tirop Meshack");

        await module.Recruitment.MoveApplicationAsync(
            application, ApplicationStatus.Rejected, "Not enough delivery experience.");

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Interviews.ScheduleAsync(
                application, InterviewKind.Technical, db.Clock.Now, [interviewer]));

        Assert.Contains("rejected", refused.Message);
    }
}
