using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Mail;
using JiranisokoTech.Domain.Clients;
using JiranisokoTech.Domain.Contracts;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Renewals;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Infrastructure.Scheduling;
using Microsoft.EntityFrameworkCore;
using Money = JiranisokoTech.Domain.Common.Money;

namespace JiranisokoTech.Tests.Infrastructure;

/// <summary>
/// The scheduled mail jobs, and the promise their own interface makes about them.
/// </summary>
/// <remarks>
/// <c>IRecurringJob</c> says in as many words that every job must be safe to run twice, and
/// that running twice finds nothing the second time. Neither mail job was: each asked "what
/// expires within the next N days" and mailed every department head about all of it, every
/// morning, so a contract ending in forty-five days produced forty-five identical emails.
/// The suite was fully green throughout, because nobody writes a test asserting that a second
/// email was not sent.
///
/// These are that test. The first one is the regression; it was watched failing against the
/// old job before the ledger existed.
/// </remarks>
public class RecurringJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Running the contracts job twice in a day sends one round of mail, not two.
    /// </summary>
    /// <remarks>
    /// The fault this whole ledger was added for. A daily job is not run exactly once a day —
    /// the container restarts, somebody presses the button on the machinery screen, the
    /// scheduler catches up after an outage — and each of those used to be a fresh round of
    /// identical emails to every department head.
    /// </remarks>
    [Fact]
    public async Task Running_the_contracts_job_twice_tells_everybody_once()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        db.Clock.Now = Now;

        await using var context = db.NewContext();
        await GiveThemAHeadAndAnExpiringContract(context, db, endsIn: 40);

        var mailer = new Recorder();
        var job = new WarnAboutExpiringContracts(context, mailer, db.Clock);

        var first = await job.RunAsync();
        var second = await job.RunAsync();

        Assert.Equal(1, mailer.Count);
        Assert.Contains("told 1", first);
        Assert.Contains("has been told", second);
    }

    /// <summary>
    /// The same contract is spoken about again when it reaches the next rung.
    /// </summary>
    /// <remarks>
    /// The other half, and the half a careless fix would break. Recording that a notice went
    /// out must not silence the contract for ever — a warning at forty days and then nothing
    /// as the date arrives is worse than the flood it replaced, because somebody stops
    /// expecting mail and the deadline passes in silence.
    /// </remarks>
    [Fact]
    public async Task The_same_contract_is_mentioned_again_at_the_next_rung()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        db.Clock.Now = Now;

        await using var context = db.NewContext();
        await GiveThemAHeadAndAnExpiringContract(context, db, endsIn: 40);

        var mailer = new Recorder();
        var job = new WarnAboutExpiringContracts(context, mailer, db.Clock);

        await job.RunAsync();
        Assert.Equal(1, mailer.Count);

        // Still at the same rung a week later: nothing more to say.
        db.Clock.Advance(TimeSpan.FromDays(7));
        await job.RunAsync();
        Assert.Equal(1, mailer.Count);

        // Now inside a fortnight of the end, which is the final rung.
        db.Clock.Advance(TimeSpan.FromDays(20));
        await job.RunAsync();
        Assert.Equal(2, mailer.Count);
    }

    /// <summary>
    /// A contract further out than the first rung is not mentioned at all.
    /// </summary>
    /// <remarks>
    /// The ladder has to be able to say nothing. A job that always found something would be
    /// the window it replaced with extra steps.
    /// </remarks>
    [Fact]
    public async Task A_contract_further_off_than_the_first_rung_is_left_alone()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        db.Clock.Now = Now;

        await using var context = db.NewContext();
        await GiveThemAHeadAndAnExpiringContract(context, db, endsIn: 200);

        var mailer = new Recorder();

        var said = await new WarnAboutExpiringContracts(context, mailer, db.Clock).RunAsync();

        Assert.Equal(0, mailer.Count);
        Assert.Contains("No contract runs out", said);
    }

    /// <summary>
    /// Nothing is recorded when there was nobody to tell.
    /// </summary>
    /// <remarks>
    /// The most consequential of these. Writing the ledger row before knowing an email went
    /// out would silence the notice permanently — so a firm that had not yet named a
    /// department head would be warned about nothing, ever, while the machinery screen
    /// reported the job succeeding every morning. Recording a notice is a claim that somebody
    /// was told.
    /// </remarks>
    [Fact]
    public async Task Nothing_is_recorded_when_there_was_nobody_to_tell()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        db.Clock.Now = Now;

        await using var context = db.NewContext();

        // A contract at a rung, and deliberately no department head at all.
        var client = Client.TakeOn("Acme Haulage", "acme");
        context.Clients.Add(client);
        await context.SaveChangesAsync();

        var contract = Contract.Draft(client.Id, "JTS-C-2026-009", "Fleet tracking", "KES");
        contract.WorthUpTo(Money.Of(1_200_000_00, "KES"));
        contract.Runs(db.Clock.Today.AddDays(-300), db.Clock.Today.AddDays(40));
        contract.Activate(db.Clock.Now);
        context.Contracts.Add(contract);
        await context.SaveChangesAsync();

        var mailer = new Recorder();

        var said = await new WarnAboutExpiringContracts(context, mailer, db.Clock).RunAsync();

        Assert.Equal(0, mailer.Count);
        Assert.Contains("no department head with a mailbox", said);

        // The important assertion: no row, so tomorrow's run still tries.
        Assert.Empty(await context.Reminders.ToListAsync());
    }

    /// <summary>
    /// A reminder about colleagues goes to the firm's mail, never to a personal inbox.
    /// </summary>
    /// <remarks>
    /// <b>This is a regression test for a live fault in both reminder jobs.</b> Each read
    /// <c>Employee.Details.PersonalEmail</c> — the address somebody types into their own
    /// profile beside their date of birth and their next of kin — and mailed the firm's
    /// certification and contract lists to it, every morning, on a schedule.
    ///
    /// Nothing failed. The suite asserted that one email was sent, which was true, and a
    /// count cannot see which address it went to. So the recorder now keeps the recipient
    /// and this asserts on it, in both directions: the work address was used, and the
    /// personal one was not.
    ///
    /// <c>MailRecipients</c> had the right answer from the day it was written — the account's
    /// own address, skipping withdrawn accounts — and these two jobs were the only senders in
    /// the system that did not ask it.
    /// </remarks>
    [Fact]
    public async Task A_reminder_about_colleagues_goes_to_the_work_address()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        db.Clock.Now = Now;

        await using var context = db.NewContext();
        await GiveThemAHeadAndAnExpiringContract(context, db, endsIn: 40);

        var mailer = new Recorder();

        await new WarnAboutExpiringContracts(context, mailer, db.Clock).RunAsync();

        var sentTo = Assert.Single(mailer.To);

        Assert.Equal("grace@jiranisokotech.co.ke", sentTo);
        Assert.DoesNotContain("personal", sentTo);
    }

    /// <summary>
    /// A head whose access has been withdrawn is not mailed, and the job says so.
    /// </summary>
    /// <remarks>
    /// Two halves, and the second is the one the original code had no way to express. A
    /// leaver still heading a department on paper cannot act on a reminder, so nothing is
    /// sent — but a firm whose reminders now reach nobody has to be able to see that, and
    /// the old jobs dropped an unreachable head in silence while reporting success.
    ///
    /// Recording the notice anyway is deliberate and is not the same fault: the ledger
    /// records that everybody reachable was told, and telling the same one person again
    /// tomorrow would not reach the others either — it would only rebuild the forty-five
    /// identical emails the ledger exists to prevent. What changes is that the sentence on
    /// the machinery screen names the gap.
    /// </remarks>
    [Fact]
    public async Task A_head_with_no_mailbox_is_counted_rather_than_dropped_quietly()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        db.Clock.Now = Now;

        await using var context = db.NewContext();
        await GiveThemAHeadAndAnExpiringContract(context, db, endsIn: 40);

        // A second department, headed by somebody with no account at all.
        var unreachable = Employee.Hire("Peter Kilonzo", new DateOnly(2023, 5, 2));
        context.Employees.Add(unreachable);
        await context.SaveChangesAsync();

        var second = Department.Open("Field operations", "field-ops");
        second.AppointHead(unreachable.Id);
        context.Departments.Add(second);
        await context.SaveChangesAsync();

        var mailer = new Recorder();

        var said = await new WarnAboutExpiringContracts(context, mailer, db.Clock).RunAsync();

        Assert.Equal(1, mailer.Count);
        Assert.Contains("told 1", said);
        Assert.Contains("1 department head has no mailbox here", said);
    }

    private static async Task GiveThemAHeadAndAnExpiringContract(
        TestDbContext context, DatabaseFixture db, int endsIn)
    {
        var head = Employee.Hire("Grace Wanjiru", new DateOnly(2024, 1, 8));

        /*
         * A work mailbox on the account, and a personal address on the record as well —
         * because the fault being guarded against is the job preferring the personal one.
         * A test where the head has only a work address would pass against the old code
         * for the wrong reason: it would send nothing and record nothing, which is a
         * different failure that happens to look like success from a distance.
         */
        head.Record(head.Details with { PersonalEmail = "grace.wanjiru.personal@example.com" });

        var account = new ApplicationUser("grace@jiranisokotech.co.ke", "Grace Wanjiru");
        context.Users.Add(account);
        head.LinkAccount(account.Id);

        context.Employees.Add(head);
        await context.SaveChangesAsync();

        var department = Department.Open("Delivery", "delivery");
        department.AppointHead(head.Id);
        context.Departments.Add(department);

        var client = Client.TakeOn("Acme Haulage", "acme");
        context.Clients.Add(client);
        await context.SaveChangesAsync();

        var contract = Contract.Draft(client.Id, "JTS-C-2026-001", "Fleet tracking", "KES");
        contract.WorthUpTo(Money.Of(1_200_000_00, "KES"));
        contract.Runs(db.Clock.Today.AddDays(-300), db.Clock.Today.AddDays(endsIn));
        contract.Activate(db.Clock.Now);
        context.Contracts.Add(contract);
        await context.SaveChangesAsync();
    }

    /// <summary>Counts what was sent, and keeps who it went to.</summary>
    /// <remarks>
    /// It counted only, until the reminder jobs were found to be mailing department heads at
    /// their personal addresses. A counter cannot see that: one email was sent either way.
    /// </remarks>
    private sealed class Recorder : IMailer
    {
        public int Count { get; private set; }

        public List<string> To { get; } = [];

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Count++;
            To.Add(message.ToAddress);

            return Task.CompletedTask;
        }
    }
}
