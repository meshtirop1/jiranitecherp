using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Mail;
using JiranisokoTech.Domain.Clients;
using JiranisokoTech.Domain.Contracts;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Renewals;
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

        // A contract at a rung, and deliberately no department head with an address.
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
        Assert.Contains("nobody with an address", said);

        // The important assertion: no row, so tomorrow's run still tries.
        Assert.Empty(await context.Reminders.ToListAsync());
    }

    private static async Task GiveThemAHeadAndAnExpiringContract(
        TestDbContext context, DatabaseFixture db, int endsIn)
    {
        var head = Employee.Hire("Grace Wanjiru", new DateOnly(2024, 1, 8));
        head.Record(head.Details with { PersonalEmail = "grace@jiranisokotech.co.ke" });
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

    /// <summary>Counts what was sent, and nothing else.</summary>
    private sealed class Recorder : IMailer
    {
        public int Count { get; private set; }

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Count++;
            return Task.CompletedTask;
        }
    }
}
