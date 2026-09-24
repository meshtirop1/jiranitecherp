using JiranisokoTech.Application.Recruitment;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Recruitment;
using JiranisokoTech.Infrastructure.People;
using JiranisokoTech.Infrastructure.Recruitment;
using JiranisokoTech.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Tests.Recruitment;

using Money = JiranisokoTech.Domain.Common.Money;

/// <summary>
/// Offers, what a candidate said, and what the firm does about it.
/// </summary>
/// <remarks>
/// Section 8, and the step section 92's hiring chain stopped at. The requisition, the advert,
/// the application, the interviews and the assessment all existed, and then the record ended at
/// a status called Offered with nothing anywhere saying what had been offered.
///
/// Three things here are worth more than the rest.
///
/// <b>The link is a secret and only its hash is stored.</b> Whoever holds it can accept a job in
/// somebody's name, so the database deliberately holds nothing that would let anybody do that —
/// exactly as with an API key.
///
/// <b>One live offer at a time.</b> Two would mean two links and two salaries, and whichever the
/// candidate accepted is the one they will say they accepted.
///
/// <b>Accepting creates nobody.</b> A person presses a button afterwards, and that button does
/// three things together: the staff record, the checklist, and the requisition's headcount.
/// </remarks>
public class OfferTests
{
    private static readonly Guid Hiring = Guid.CreateVersion7();

    /// <summary>
    /// The link works once and is not recoverable from what is stored.
    /// </summary>
    /// <remarks>
    /// Both halves. The first is what makes the feature work at all; the second is what makes it
    /// safe, and it is only demonstrable by looking at the row and failing to find the secret in
    /// it.
    /// </remarks>
    [Fact]
    public async Task The_acceptance_link_is_a_secret_and_only_its_hash_is_kept()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var application = await Applied(fixture, context);

        var written = await service.WriteAsync(
            application,
            "Delivery engineer",
            Money.Of(320_000_00, "KES"),
            PayFrequency.Monthly,
            fixture.Clock.Today.AddDays(30),
            fixture.Clock.Today.AddDays(7),
            "Three months probation, one month's notice either way.",
            Hiring);

        var stored = await context.Offers.SingleAsync();

        Assert.DoesNotContain(written.Secret, stored.TokenHash);
        Assert.Equal(64, stored.TokenHash.Length);

        var found = await service.BehindAsync(written.Secret);

        Assert.NotNull(found);
        Assert.Equal(stored.Id, found!.Id);

        Assert.Null(await service.BehindAsync("not-the-secret"));
    }

    /// <summary>One live offer per application.</summary>
    [Fact]
    public async Task A_second_live_offer_is_refused()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var application = await Applied(fixture, context);

        await Write(service, application, fixture);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Write(service, application, fixture));

        Assert.Contains("already a draft offer", refusal.Message);
    }

    /// <summary>
    /// A draft can be corrected; a sent one cannot.
    /// </summary>
    /// <remarks>
    /// The whole reason for having a draft state. The candidate is reading a page, and a page
    /// that changes under them between reading and accepting is not an offer anybody could rely
    /// on — the terms they accepted have to be the terms they saw.
    /// </remarks>
    [Fact]
    public async Task Terms_freeze_when_the_offer_is_sent()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var application = await Applied(fixture, context);
        var written = await Write(service, application, fixture);

        await service.ReviseAsync(
            written.Offer.Id,
            "Senior delivery engineer",
            Money.Of(380_000_00, "KES"),
            PayFrequency.Monthly,
            fixture.Clock.Today.AddDays(30),
            fixture.Clock.Today.AddDays(7),
            "Three months probation.");

        Assert.Equal("Senior delivery engineer", written.Offer.JobTitle);

        await service.SendAsync(written.Offer.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ReviseAsync(
                written.Offer.Id,
                "Something else",
                Money.Of(1_00, "KES"),
                PayFrequency.Monthly,
                fixture.Clock.Today.AddDays(30),
                fixture.Clock.Today.AddDays(7),
                "Different terms."));
    }

    /// <summary>A draft cannot be accepted, however the link was come by.</summary>
    [Fact]
    public async Task An_offer_that_has_not_been_sent_cannot_be_accepted()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var application = await Applied(fixture, context);
        var written = await Write(service, application, fixture);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AcceptAsync(written.Secret, "Charity Jepchirchir"));
    }

    /// <summary>
    /// An offer past its closing date is refused, and the refusal says what to do.
    /// </summary>
    /// <remarks>
    /// Deliberate rather than generous. The firm may well still want somebody who answers three
    /// weeks late — but that is a decision somebody makes by moving the date, not something a
    /// candidate does by finding an old email.
    /// </remarks>
    [Fact]
    public async Task An_offer_that_has_closed_cannot_be_accepted_until_somebody_extends_it()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var application = await Applied(fixture, context);
        var written = await Write(service, application, fixture);

        await service.SendAsync(written.Offer.Id);

        fixture.Clock.Advance(TimeSpan.FromDays(10));

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AcceptAsync(written.Secret, "Charity Jepchirchir"));

        Assert.Contains("closed on", refusal.Message);

        await service.CloseOnAsync(written.Offer.Id, fixture.Clock.Today.AddDays(3));
        await service.AcceptAsync(written.Secret, "Charity Jepchirchir");

        Assert.Equal(OfferStatus.Accepted, written.Offer.Status);
        Assert.Equal("Charity Jepchirchir", written.Offer.SignedName);
    }

    /// <summary>The closing date moves out, never in.</summary>
    /// <remarks>
    /// Bringing it forward would withdraw an offer somebody is still reading without saying so,
    /// which is the one way this screen could quietly take something back.
    /// </remarks>
    [Fact]
    public async Task The_closing_date_cannot_be_brought_forward()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var application = await Applied(fixture, context);
        var written = await Write(service, application, fixture);

        await service.SendAsync(written.Offer.Id);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.CloseOnAsync(written.Offer.Id, fixture.Clock.Today));
    }

    /// <summary>Accepting creates no staff record on its own.</summary>
    /// <remarks>
    /// The same reasoning as an opportunity being won creating no client and no project. A start
    /// date that moves by a fortnight between acceptance and arrival is the ordinary case, and a
    /// record created the moment somebody clicked would carry the wrong one.
    /// </remarks>
    [Fact]
    public async Task Accepting_creates_nobody()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var application = await Applied(fixture, context);
        var written = await Write(service, application, fixture);

        await service.SendAsync(written.Offer.Id);
        await service.AcceptAsync(written.Secret, "Charity Jepchirchir");

        Assert.Empty(await context.Employees.ToListAsync());
        Assert.Empty(await context.Onboardings.ToListAsync());
    }

    /// <summary>
    /// Taking somebody on does three things at once, and the terms come off the offer.
    /// </summary>
    /// <remarks>
    /// Any two of the three without the third is a state somebody has to notice and fix: a
    /// person on the staff list with no checklist, or a requisition still advertising a post
    /// that is filled. The salary is asserted because retyping a figure that is already recorded
    /// is how it gets retyped wrong — three money boxes in this application once stored a
    /// hundredth of what somebody meant.
    /// </remarks>
    [Fact]
    public async Task Taking_somebody_on_makes_the_record_the_checklist_and_the_hire()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var application = await Applied(fixture, context);
        var written = await Write(service, application, fixture);

        await service.SendAsync(written.Offer.Id);
        await service.AcceptAsync(written.Secret, "Charity Jepchirchir");

        var employee = await service.TakeOnAsync(written.Offer.Id);

        Assert.Equal("Charity Jepchirchir", employee.FullName);
        Assert.Equal("Delivery engineer", employee.JobTitle);
        Assert.Equal(Money.Of(320_000_00, "KES"), employee.Terms.Salary);
        Assert.Equal(written.Offer.StartsOn, employee.StartsOn);

        Assert.True(await context.Onboardings.AnyAsync(one => one.EmployeeId == employee.Id));

        var applied = await context.Applications.SingleAsync();

        Assert.Equal(ApplicationStatus.Hired, applied.Status);
    }

    /// <summary>The same offer cannot become two people.</summary>
    [Fact]
    public async Task One_offer_becomes_one_person()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var application = await Applied(fixture, context);
        var written = await Write(service, application, fixture);

        await service.SendAsync(written.Offer.Id);
        await service.AcceptAsync(written.Secret, "Charity Jepchirchir");
        await service.TakeOnAsync(written.Offer.Id);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.TakeOnAsync(written.Offer.Id));

        Assert.Contains("already been made", refusal.Message);
        Assert.Equal(1, await context.Employees.CountAsync());
    }

    /// <summary>Declining is recorded, and the reason is optional.</summary>
    /// <remarks>
    /// Optional unlike a withdrawal's, because somebody turning down a job owes the firm
    /// nothing, and a required box would be answered with a full stop.
    /// </remarks>
    [Fact]
    public async Task Declining_needs_no_reason()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var application = await Applied(fixture, context);
        var written = await Write(service, application, fixture);

        await service.SendAsync(written.Offer.Id);
        await service.DeclineAsync(written.Secret, null);

        Assert.Equal(OfferStatus.Declined, written.Offer.Status);
        Assert.Null(written.Offer.Outcome);
    }

    /// <summary>Withdrawing one does need a reason.</summary>
    [Fact]
    public async Task Withdrawing_needs_a_reason()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var application = await Applied(fixture, context);
        var written = await Write(service, application, fixture);

        await service.SendAsync(written.Offer.Id);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.WithdrawAsync(written.Offer.Id, "  "));

        await service.WithdrawAsync(written.Offer.Id, "the post has been put on hold");

        Assert.Equal(OfferStatus.Withdrawn, written.Offer.Status);
    }

    /// <summary>An accepted offer is not withdrawn; that is a conversation.</summary>
    [Fact]
    public async Task An_accepted_offer_cannot_be_withdrawn_here()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var application = await Applied(fixture, context);
        var written = await Write(service, application, fixture);

        await service.SendAsync(written.Offer.Id);
        await service.AcceptAsync(written.Secret, "Charity Jepchirchir");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.WithdrawAsync(written.Offer.Id, "we changed our minds"));
    }

    private static OfferService Service(DatabaseFixture fixture, TestDbContext context) =>
        new(new RecruitmentRepository(context), new PeopleRepository(context), fixture.Clock);

    private static Task<WrittenOffer> Write(
        OfferService service, Guid application, DatabaseFixture fixture) =>
        service.WriteAsync(
            application,
            "Delivery engineer",
            Money.Of(320_000_00, "KES"),
            PayFrequency.Monthly,
            fixture.Clock.Today.AddDays(30),
            fixture.Clock.Today.AddDays(7),
            "Three months probation, one month's notice either way.",
            Hiring);

    /// <summary>A requisition, an advert, a candidate and an application against it.</summary>
    private static async Task<Guid> Applied(DatabaseFixture fixture, TestDbContext context)
    {
        var requisition = JobRequisition.Raise(
            "Delivery engineer", null, 1, "The despatch team is one short", Guid.CreateVersion7());

        requisition.Submit(fixture.Clock.Now);
        requisition.Approved(fixture.Clock.Now);

        var posting = JobPosting.Draft(
            requisition.Id,
            "Delivery engineer",
            "Come and work here",
            "You would be building the despatch board.");

        var candidate = Candidate.Of(
            "Charity Jepchirchir", "charity@example.com", null, fixture.Clock.Now);

        var application = JobApplication.Receive(
            posting.Id, candidate.Id, fixture.Clock.Now);

        application.MoveTo(ApplicationStatus.Screening, fixture.Clock.Now);
        application.MoveTo(ApplicationStatus.Interviewing, fixture.Clock.Now);
        application.MoveTo(ApplicationStatus.Offered, fixture.Clock.Now);

        context.Requisitions.Add(requisition);
        context.Postings.Add(posting);
        context.Candidates.Add(candidate);
        context.Applications.Add(application);

        await context.SaveChangesAsync();

        return application.Id;
    }
}
