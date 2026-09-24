using JiranisokoTech.Application.Mail;
using JiranisokoTech.Application.Platform;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Domain.Platform;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Renewals;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Infrastructure.Platform;
using JiranisokoTech.Infrastructure.Scheduling;
using JiranisokoTech.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Tests.Platform;

/// <summary>
/// The catalogue of what the firm runs, the register of what it runs on, and the one part of it
/// that acts.
/// </summary>
/// <remarks>
/// Section 14. Almost all of it is a record of what somebody typed — nothing reads a cloud
/// account, a DNS zone or a certificate — so the tests worth writing are about the two places
/// where the record has consequences.
///
/// <b>One name in the catalogue.</b> Two entries called "despatch board" means every incident
/// after that is filed against whichever one the person picked, and the history of the thing
/// splits in half with nobody noticing.
///
/// <b>The expiry ladder.</b> A certificate that lapses takes the site down at a moment nobody
/// chose. The job that warns about it has to be safe to run every morning, which means the
/// second run of the day finds nothing — a property that is easy to claim and was wrong in this
/// codebase's first two reminder jobs.
/// </remarks>
public class EstateTests
{
    /// <summary>Two services cannot share a name, in any case.</summary>
    [Fact]
    public async Task Two_services_cannot_share_a_name()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var estate = new EstateService(new EstateRepository(context), fixture.Clock);

        await estate.AddServiceAsync(
            "Despatch board", "Shows depots where vehicles are", HowCritical.Critical);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => estate.AddServiceAsync(
                "  despatch BOARD ", "The same thing again", HowCritical.Minor));

        Assert.Contains("already a service", refusal.Message);
    }

    /// <summary>
    /// Retiring a service keeps it, and keeps what it ran on.
    /// </summary>
    /// <remarks>
    /// Both halves. Incidents point at the catalogue entry, so deleting one would take the name
    /// off every incident it ever had; and a server does not disappear because the product it
    /// served was discontinued — which is exactly the machine somebody is hunting for when they
    /// ask what is still costing money.
    /// </remarks>
    [Fact]
    public async Task Retiring_a_service_keeps_it_and_what_it_ran_on()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var estate = new EstateService(new EstateRepository(context), fixture.Clock);

        var service = await estate.AddServiceAsync(
            "Despatch board", "Shows depots where vehicles are", HowCritical.Critical);

        await estate.RecordAsync(
            "despatch-1.jiranisokotech.co.ke",
            ResourceKind.Server,
            DeploymentEnvironment.Production,
            "HostPinnacle",
            service.Id);

        await estate.RetireServiceAsync(service.Id);

        var stored = await context.Services.SingleAsync();

        Assert.False(stored.IsLive);
        Assert.Equal(1, await context.Resources.CountAsync());
    }

    /// <summary>
    /// The warning job tells somebody once per rung and not again.
    /// </summary>
    /// <remarks>
    /// The property every recurring job in this system claims and two of them did not have: safe
    /// to run twice, and the second run finds nothing. Before that was fixed, the qualifications
    /// job mailed every department head about everything lapsing every single morning.
    /// </remarks>
    [Fact]
    public async Task Something_running_out_is_warned_about_once_per_rung()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var estate = new EstateService(new EstateRepository(context), fixture.Clock);
        var post = new CountingMailer();

        await Head(fixture, context);

        await estate.RecordAsync(
            "jiranisokotech.co.ke",
            ResourceKind.Domain,
            DeploymentEnvironment.Production,
            "HostPinnacle",
            expiresOn: fixture.Clock.Today.AddDays(30));

        var job = new WarnAboutExpiringResources(context, post, fixture.Clock);

        var first = await job.RunAsync();
        var again = await job.RunAsync();

        Assert.Equal(1, post.Sent);
        Assert.Contains("1", first);
        Assert.Contains("everybody who needs telling has been told", again);
    }

    /// <summary>
    /// The next rung down warns again.
    /// </summary>
    /// <remarks>
    /// The other half of the ladder, and the reason there is one: a notice sent once at thirty
    /// days is forgotten by the time it matters, and a notice sent every day for thirty days is
    /// a filter rule.
    /// </remarks>
    [Fact]
    public async Task The_next_rung_warns_again()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var estate = new EstateService(new EstateRepository(context), fixture.Clock);
        var post = new CountingMailer();

        await Head(fixture, context);

        await estate.RecordAsync(
            "star.jiranisokotech.co.ke",
            ResourceKind.Certificate,
            DeploymentEnvironment.Production,
            "Let's Encrypt",
            expiresOn: fixture.Clock.Today.AddDays(30));

        var job = new WarnAboutExpiringResources(context, post, fixture.Clock);

        await job.RunAsync();

        fixture.Clock.Advance(TimeSpan.FromDays(23));

        await job.RunAsync();

        Assert.Equal(2, post.Sent);

        var rungs = await context.Reminders
            .Where(one => one.Kind == ReminderKind.ResourceExpiring)
            .Select(one => one.Stage)
            .ToListAsync();

        Assert.Equal([ReminderStage.First, ReminderStage.Second], rungs.Order());
    }

    /// <summary>Something retired is not warned about.</summary>
    /// <remarks>
    /// Because a domain the firm gave up is a domain it does not want reminding to renew, and a
    /// warning list with dead entries on it is one people stop reading.
    /// </remarks>
    [Fact]
    public async Task Something_the_firm_no_longer_has_is_not_warned_about()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var estate = new EstateService(new EstateRepository(context), fixture.Clock);
        var post = new CountingMailer();

        await Head(fixture, context);

        var gone = await estate.RecordAsync(
            "old.jiranisokotech.co.ke",
            ResourceKind.Domain,
            DeploymentEnvironment.Production,
            expiresOn: fixture.Clock.Today.AddDays(30));

        await estate.RetireResourceAsync(gone.Id);

        var said = await new WarnAboutExpiringResources(context, post, fixture.Clock).RunAsync();

        Assert.Equal(0, post.Sent);
        Assert.Contains("Nothing runs out", said);
    }

    /// <summary>
    /// Nothing is written down when nobody could be told.
    /// </summary>
    /// <remarks>
    /// The rule that keeps a reminder ladder honest. A row recorded against an email that never
    /// went out silences that rung for good — so a firm with no department head configured would
    /// be warned about nothing, for ever, by a job reporting success every morning.
    /// </remarks>
    [Fact]
    public async Task Nothing_is_recorded_when_there_was_nobody_to_tell()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var estate = new EstateService(new EstateRepository(context), fixture.Clock);
        var post = new CountingMailer();

        await estate.RecordAsync(
            "jiranisokotech.co.ke",
            ResourceKind.Domain,
            DeploymentEnvironment.Production,
            expiresOn: fixture.Clock.Today.AddDays(30));

        var said = await new WarnAboutExpiringResources(context, post, fixture.Clock).RunAsync();

        Assert.Equal(0, post.Sent);
        Assert.Contains("no department head", said);
        Assert.Equal(0, await context.Reminders.CountAsync());
    }

    /// <summary>
    /// A head of department with a work mailbox, so the job has somebody to tell.
    /// </summary>
    /// <remarks>
    /// The address has to be on the account rather than on the staff record, because that is
    /// where these notices go — a lesson this codebase learned by finding its reminder jobs
    /// mailing the firm's certification lists to people's personal addresses.
    /// </remarks>
    private static async Task Head(DatabaseFixture fixture, TestDbContext context)
    {
        var head = Employee.Hire(
            "Charity Jepchirchir", fixture.Clock.Today, null, "Head of engineering");

        var account = new ApplicationUser(
            "charity@jiranisokotech.co.ke", "Charity Jepchirchir");

        context.Users.Add(account);
        head.LinkAccount(account.Id);
        context.Employees.Add(head);
        await context.SaveChangesAsync();

        var department = Department.Open("Engineering", "engineering");
        department.AppointHead(head.Id);
        context.Departments.Add(department);

        await context.SaveChangesAsync();
    }

    private sealed class CountingMailer : IMailer
    {
        public int Sent { get; private set; }

        public Task SendAsync(
            EmailMessage message, CancellationToken cancellationToken = default)
        {
            Sent++;

            return Task.CompletedTask;
        }
    }
}
