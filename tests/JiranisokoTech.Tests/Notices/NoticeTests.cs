using JiranisokoTech.Application.Mail;
using JiranisokoTech.Application.Notices;
using JiranisokoTech.Domain.Approvals;
using JiranisokoTech.Domain.Notices;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Infrastructure.Notices;
using JiranisokoTech.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace JiranisokoTech.Tests.Notices;

/// <summary>
/// Telling one person that one thing happened, and what they may turn off.
/// </summary>
/// <remarks>
/// Sections 32 and 59. Three rules carry the weight.
///
/// <b>The notice is always recorded and only the email is optional.</b> What somebody chooses is
/// what interrupts them, not what the system remembers — a record with holes in it is worse than
/// no record, because months later nobody can tell whether a thing did not happen or merely was
/// not written down.
///
/// <b>Work leaving a list is its own kind.</b> The event carries both ends on purpose, because
/// work quietly leaving somebody's list is the most common way it gets dropped.
///
/// <b>A notice belongs to one person.</b> Marking one read is a write to a record about
/// somebody, so the ownership check is in the service rather than on the page — a notice
/// identifier in a URL is a thing anybody can type.
/// </remarks>
public class NoticeTests
{
    /// <summary>Being given work tells the person who got it and the person who lost it.</summary>
    [Fact]
    public async Task Moving_work_tells_both_ends()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var post = new CountingMailer();
        var service = Service(fixture, context, post);

        var amina = await Person(fixture, context, "Amina Hassan", "amina@jiranisokotech.co.ke");
        var brian = await Person(fixture, context, "Brian Otieno", "brian@jiranisokotech.co.ke");

        await new TellPeopleTheirWorkMoved(service).HandleAsync(
            new WorkItemAssigned(Guid.CreateVersion7(), "Fix the despatch board", amina, brian));

        var hers = await service.ForAsync(amina);
        var his = await service.ForAsync(brian);

        Assert.Equal(NoticeKind.WorkTaken, Assert.Single(hers).Kind);
        Assert.Equal(NoticeKind.WorkGiven, Assert.Single(his).Kind);

        /*
         * One email, not two. Work arriving interrupts somebody; work leaving does not, and a
         * firm where reassigning three items sends six emails is a firm where people filter the
         * address — which loses the ones that mattered.
         */
        Assert.Equal(1, post.Sent);
    }

    /// <summary>Unassigning tells one person, once.</summary>
    /// <remarks>
    /// Because taking work off somebody and giving it to nobody is one thing happening, and a
    /// second notice saying the same thing to the same person is how a centre becomes noise.
    /// </remarks>
    [Fact]
    public async Task Taking_work_off_somebody_tells_them_once()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context, new CountingMailer());

        var amina = await Person(fixture, context, "Amina Hassan", "amina@jiranisokotech.co.ke");

        await new TellPeopleTheirWorkMoved(service).HandleAsync(
            new WorkItemAssigned(Guid.CreateVersion7(), "Fix the despatch board", amina, null));

        var only = Assert.Single(await service.ForAsync(amina));

        Assert.Equal(NoticeKind.WorkTaken, only.Kind);
        Assert.Contains("no longer assigned", only.Subject);
    }

    /// <summary>A settled request tells whoever asked, in words.</summary>
    /// <remarks>
    /// The action on the event is a machine-readable string like requisition.approve, and
    /// putting that in front of somebody is how a notice reads as a log line.
    /// </remarks>
    [Fact]
    public async Task A_settled_request_tells_whoever_asked()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context, new CountingMailer());

        var amina = await Person(fixture, context, "Amina Hassan", "amina@jiranisokotech.co.ke");

        await new TellSomebodyTheirRequestWasSettled(service).HandleAsync(
            new ApprovalSettled(
                Guid.CreateVersion7(),
                "requisition",
                Guid.CreateVersion7(),
                "requisition.approve",
                ApprovalStatus.Approved,
                null,
                fixture.Clock.Now,
                amina));

        var only = Assert.Single(await service.ForAsync(amina));

        Assert.Equal("Your request to recruit was approved.", only.Subject);
    }

    /// <summary>
    /// Turning email off still records the notice.
    /// </summary>
    /// <remarks>
    /// The decision the whole of section 59 turns on, and the one worth a test rather than a
    /// comment: it would be very easy to implement "mute" as "do not write it down", and the
    /// consequence would only show up months later when somebody asked why a record had a hole
    /// in it.
    /// </remarks>
    [Fact]
    public async Task Turning_email_off_still_records_the_notice()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var post = new CountingMailer();
        var service = Service(fixture, context, post);

        var amina = await Person(fixture, context, "Amina Hassan", "amina@jiranisokotech.co.ke");

        await service.SetRuleAsync(amina, NoticeKind.WorkGiven, false);

        await service.TellAsync(amina, NoticeKind.WorkGiven, "You were given something.");

        Assert.Single(await service.ForAsync(amina));
        Assert.Equal(0, post.Sent);
        Assert.Equal(1, await service.UnreadForAsync(amina));
    }

    /// <summary>Somebody with no mailbox still gets the notice.</summary>
    /// <remarks>
    /// An employee and an account are separate things here on purpose, so a person with no
    /// sign-in is an ordinary state — and their record of what happened should not depend on it.
    /// </remarks>
    [Fact]
    public async Task Somebody_with_no_mailbox_still_gets_the_notice()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var post = new CountingMailer();
        var service = Service(fixture, context, post);

        var nobody = await Person(fixture, context, "Peter Kilonzo", null);

        await service.TellAsync(nobody, NoticeKind.WorkGiven, "You were given something.");

        Assert.Single(await service.ForAsync(nobody));
        Assert.Equal(0, post.Sent);
    }

    /// <summary>
    /// A mail failure does not lose the notice or throw.
    /// </summary>
    /// <remarks>
    /// Because the thing that caused the notice has already happened and the notice is already
    /// written. Letting a mail failure escape a handler would roll it back and have the outbox
    /// try the whole thing again — which would write the notice twice, putting a duplicate on
    /// somebody's screen because a mail server was slow.
    /// </remarks>
    [Fact]
    public async Task A_mail_failure_keeps_the_notice_and_does_not_throw()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context, new BrokenMailer());

        var amina = await Person(fixture, context, "Amina Hassan", "amina@jiranisokotech.co.ke");

        await service.TellAsync(amina, NoticeKind.WorkGiven, "You were given something.");

        Assert.Single(await service.ForAsync(amina));
    }

    /// <summary>Somebody else cannot mark your notice read.</summary>
    [Fact]
    public async Task Somebody_else_cannot_read_your_notice()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context, new CountingMailer());

        var amina = await Person(fixture, context, "Amina Hassan", "amina@jiranisokotech.co.ke");
        var brian = await Person(fixture, context, "Brian Otieno", "brian@jiranisokotech.co.ke");

        await service.TellAsync(amina, NoticeKind.WorkGiven, "You were given something.");

        var hers = Assert.Single(await service.ForAsync(amina));

        await service.ReadAsync(brian, hers.Id);

        Assert.Equal(1, await service.UnreadForAsync(amina));

        await service.ReadAsync(amina, hers.Id);

        Assert.Equal(0, await service.UnreadForAsync(amina));
    }

    /// <summary>
    /// The link in an email is one somebody outside this system can follow.
    /// </summary>
    /// <remarks>
    /// The first version sent "/work/01a0d205…", which is a link to nothing in an inbox. The
    /// notice keeps the relative path because the page that shows it is already here; only the
    /// email needs the address the firm answers on, and the dispatcher that sends it has no
    /// request to read one from.
    /// </remarks>
    [Fact]
    public async Task The_link_in_an_email_can_be_followed_from_outside()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var post = new CountingMailer();
        var service = Service(fixture, context, post);

        var amina = await Person(fixture, context, "Amina Hassan", "amina@jiranisokotech.co.ke");

        await service.TellAsync(
            amina, NoticeKind.WorkGiven, "You were given something.", "/work/14");

        Assert.Contains("https://erp.jiranisokotech.co.ke/work/14", post.LastBody);

        var stored = Assert.Single(await service.ForAsync(amina));

        Assert.Equal("/work/14", stored.Link);
    }

    /// <summary>Every kind comes back, chosen or not.</summary>
    /// <remarks>
    /// A preference screen that only showed what somebody had already changed would start empty
    /// and teach nothing about what it can do.
    /// </remarks>
    [Fact]
    public async Task Every_kind_has_an_answer_whether_or_not_it_was_chosen()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context, new CountingMailer());

        var amina = await Person(fixture, context, "Amina Hassan", "amina@jiranisokotech.co.ke");

        var rules = await service.RulesForAsync(amina);

        Assert.Equal(Enum.GetValues<NoticeKind>().Length, rules.Count);
        Assert.False(rules[NoticeKind.WorkTaken]);
        Assert.True(rules[NoticeKind.WorkGiven]);
    }

    private static NoticeService Service(
        DatabaseFixture fixture, TestDbContext context, IMailer mailer) =>
        new(
            new NoticeRepository(context),
            mailer,
            new Somewhere(),
            fixture.Clock,
            NullLogger<NoticeService>.Instance);

    private static async Task<Guid> Person(
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

    /// <summary>
    /// A configured address, so that a link in an email is one somebody can follow.
    /// </summary>
    /// <remarks>
    /// Here because the first version of this feature put "/work/01a0d205…" in an inbox, which
    /// is a link to nothing. The notice itself keeps the relative path, because the page that
    /// shows it is already here.
    /// </remarks>
    private sealed class Somewhere : IWhereThisLives
    {
        public string? Reachable(string? path) =>
            path is null ? null : "https://erp.jiranisokotech.co.ke" + path;
    }

    private sealed class CountingMailer : IMailer
    {
        public int Sent { get; private set; }

        /// <summary>The last link sent, so a test can look at it.</summary>
        public string? LastBody { get; private set; }

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Sent++;
            LastBody = message.TextBody;

            return Task.CompletedTask;
        }
    }

    private sealed class BrokenMailer : IMailer
    {
        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The mail server is not answering.");
    }
}
