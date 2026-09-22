using JiranisokoTech.Application.Business;
using JiranisokoTech.Application.People;
using JiranisokoTech.Application.Recruitment;
using JiranisokoTech.Application.Work;
using JiranisokoTech.Domain.Clients;
using JiranisokoTech.Domain.Money;
using JiranisokoTech.Domain.Recruitment;
using JiranisokoTech.Domain.Time;
using JiranisokoTech.Infrastructure.Business;
using JiranisokoTech.Infrastructure.People;
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

        public PeopleService People => new(new PeopleRepository(_context));

        public WorkService Work =>
            new(new WorkRepository(_context), new PeopleRepository(_context), db.Clock);

        public RecruitmentService Recruitment => new(
            new RecruitmentRepository(_context),
            new PeopleRepository(_context),
            new NoCvStore(),
            db.Clock);

        public ClientService Clients => new(Repository);

        public TimesheetService Timesheets =>
            new(Repository, new PeopleRepository(_context), db.Clock);

        public LeaveService Leave => new(Repository, db.Clock);

        public ExpenseService Expenses => new(Repository, db.Clock);

        public InvoiceService Invoices => new(Repository, db.Clock);

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
        var yesterday = db.Clock.Today.AddDays(-1);

        var sick = await module.Leave.AskForAsync(
            engineer, LeaveKind.Sick, yesterday, yesterday, "Food poisoning.");

        Assert.Equal(LeaveStatus.Draft, sick.Status);

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
