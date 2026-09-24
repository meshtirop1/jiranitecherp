using JiranisokoTech.Application.Business;
using JiranisokoTech.Application.Mail;
using JiranisokoTech.Domain.Contracts;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Renewals;
using JiranisokoTech.Infrastructure.Business;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Infrastructure.Scheduling;
using JiranisokoTech.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Tests.Business;

/// <summary>
/// The paper the firm has signed with people who are not clients.
/// </summary>
/// <remarks>
/// Section 17's second half. Client contracts had been here since the section was built;
/// employment contracts, vendor agreements and NDAs had not, so the only place an employment
/// contract existed was as a file on a staff record with nothing knowing when it ran out.
///
/// The rules worth testing are the two that make this more than a filing cabinet: a reference
/// identifies exactly one piece of paper, and something with an end date produces a reminder —
/// on the same ladder a client contract gets, and safe to run every morning.
///
/// <b>Replaced is not ended</b>, and that is the third. An employment contract replaced after a
/// pay rise did not expire and was not terminated; deleting it would leave a personnel file that
/// begins in the middle.
/// </remarks>
public class AgreementTests
{
    [Fact]
    public async Task Two_agreements_cannot_share_a_reference()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        await service.DraftAsync(
            AgreementKind.Nda, "JTS-NDA-2026-004", "Mutual non-disclosure", "Safaricom PLC");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DraftAsync(
                AgreementKind.Vendor, " jts-nda-2026-004 ", "Something else", "Somebody"));

        Assert.Contains("already on file", refusal.Message);
    }

    /// <summary>An agreement cannot end before it starts.</summary>
    /// <remarks>
    /// The typo a pair of date fields invites, and one worth refusing: an agreement that ended
    /// before it began would sit in the expired list for ever with nobody able to say what it
    /// was.
    /// </remarks>
    [Fact]
    public async Task An_agreement_cannot_end_before_it_starts()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var nda = await service.DraftAsync(
            AgreementKind.Nda, "JTS-NDA-2026-004", "Mutual non-disclosure", "Safaricom PLC");

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.RunsAsync(
                nda.Id, fixture.Clock.Today, fixture.Clock.Today.AddDays(-1), null));
    }

    /// <summary>
    /// Signing does not need a date.
    /// </summary>
    /// <remarks>
    /// Plenty of agreements are signed before anybody decides when they start, and a system that
    /// refused would have somebody type a date they did not mean in order to record a thing that
    /// had happened.
    /// </remarks>
    [Fact]
    public async Task Signing_does_not_need_a_date()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var nda = await service.DraftAsync(
            AgreementKind.Nda, "JTS-NDA-2026-004", "Mutual non-disclosure", "Safaricom PLC");

        await service.SignedAsync(nda.Id);

        Assert.True(nda.IsInForce);
        Assert.Null(nda.EndsOn);
    }

    /// <summary>
    /// Replacing one keeps both, and the replacement has to exist.
    /// </summary>
    /// <remarks>
    /// The whole value of recording a replacement is that somebody reading the old one can find
    /// the new one — a reference to a piece of paper nobody wrote down is a dead end with a date
    /// on it.
    /// </remarks>
    [Fact]
    public async Task Replacing_one_keeps_both_and_names_the_replacement()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var employee = await Somebody(fixture, context, "Charity Jepchirchir", null);

        var first = await service.DraftAsync(
            AgreementKind.Employment,
            "JTS-EMP-2026-002",
            "Contract of employment",
            "Charity Jepchirchir",
            employee);

        await service.SignedAsync(first.Id);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SupersedeAsync(first.Id, "JTS-EMP-2027-011"));

        Assert.Contains("Write the new agreement down first", refusal.Message);

        await service.DraftAsync(
            AgreementKind.Employment,
            "JTS-EMP-2027-011",
            "Contract of employment, revised",
            "Charity Jepchirchir",
            employee);

        await service.SupersedeAsync(first.Id, "JTS-EMP-2027-011");

        Assert.Equal(AgreementState.Superseded, first.State);
        Assert.Contains("JTS-EMP-2027-011", first.Outcome);
        Assert.Equal(2, await context.Agreements.CountAsync());

        // Both are on the person's record, which is the point of keeping the first.
        Assert.Equal(2, (await service.ForEmployeeAsync(employee)).Count);
    }

    /// <summary>
    /// Something running out is warned about once per rung.
    /// </summary>
    /// <remarks>
    /// Safe to run every morning, and the second run of the day finds nothing. That property is
    /// claimed by every recurring job here and two of them did not have it — before that was
    /// fixed, the qualifications job mailed every department head about everything lapsing every
    /// single day.
    /// </remarks>
    [Fact]
    public async Task Something_running_out_is_warned_about_once_per_rung()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);
        var post = new CountingMailer();

        await Head(fixture, context);

        var nda = await service.DraftAsync(
            AgreementKind.Nda, "JTS-NDA-2026-004", "Mutual non-disclosure", "Safaricom PLC");

        await service.SignedAsync(nda.Id);
        await service.RunsAsync(
            nda.Id, fixture.Clock.Today.AddYears(-1), fixture.Clock.Today.AddDays(90), 30);

        var job = new WarnAboutExpiringAgreements(context, post, fixture.Clock);

        var first = await job.RunAsync();
        var again = await job.RunAsync();

        Assert.Equal(1, post.Sent);
        Assert.Contains("1", first);
        Assert.Contains("everybody who needs telling has been told", again);
    }

    /// <summary>
    /// A draft with an end date is warned about too.
    /// </summary>
    /// <remarks>
    /// Deliberately. A draft with an end date three weeks away is a piece of paper somebody has
    /// not got signed yet, which is exactly the thing worth chasing — filtering it out would
    /// mean the system goes quiet about the one state that needs it.
    /// </remarks>
    [Fact]
    public async Task A_draft_that_runs_out_is_warned_about_too()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);
        var post = new CountingMailer();

        await Head(fixture, context);

        var vendor = await service.DraftAsync(
            AgreementKind.Vendor, "JTS-VEN-2026-007", "Hosting", "HostPinnacle");

        await service.RunsAsync(
            vendor.Id, fixture.Clock.Today.AddYears(-1), fixture.Clock.Today.AddDays(90), null);

        await new WarnAboutExpiringAgreements(context, post, fixture.Clock).RunAsync();

        Assert.Equal(1, post.Sent);
    }

    /// <summary>Something replaced or ended is not warned about.</summary>
    [Fact]
    public async Task Something_already_over_is_not_warned_about()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);
        var post = new CountingMailer();

        await Head(fixture, context);

        var vendor = await service.DraftAsync(
            AgreementKind.Vendor, "JTS-VEN-2026-007", "Hosting", "HostPinnacle");

        await service.RunsAsync(
            vendor.Id, fixture.Clock.Today.AddYears(-1), fixture.Clock.Today.AddDays(90), null);

        await service.EndAsync(vendor.Id, "the supplier was changed");

        var said = await new WarnAboutExpiringAgreements(
            context, post, fixture.Clock).RunAsync();

        Assert.Equal(0, post.Sent);
        Assert.Contains("No agreement runs out", said);
    }

    private static AgreementService Service(DatabaseFixture fixture, TestDbContext context) =>
        new(new AgreementRepository(context), fixture.Clock);

    private static async Task<Guid> Somebody(
        DatabaseFixture fixture, TestDbContext context, string name, string? mailbox)
    {
        var employee = Employee.Hire(name, fixture.Clock.Today, null, "Delivery engineer");

        if (mailbox is not null)
        {
            var account = new ApplicationUser(mailbox, name);

            context.Users.Add(account);
            employee.LinkAccount(account.Id);
        }

        context.Employees.Add(employee);
        await context.SaveChangesAsync();

        return employee.Id;
    }

    /// <summary>A head of department with a work mailbox, so the job has somebody to tell.</summary>
    private static async Task Head(DatabaseFixture fixture, TestDbContext context)
    {
        var head = await Somebody(
            fixture, context, "Grace Wanjiru", "grace@jiranisokotech.co.ke");

        var department = Department.Open("Delivery", "delivery");

        department.AppointHead(head);
        context.Departments.Add(department);

        await context.SaveChangesAsync();
    }

    private sealed class CountingMailer : IMailer
    {
        public int Sent { get; private set; }

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Sent++;

            return Task.CompletedTask;
        }
    }
}
