using JiranisokoTech.Application.Notices;
using JiranisokoTech.Domain.Notices;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Infrastructure.Notices;
using JiranisokoTech.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Tests.Notices;

/// <summary>
/// The firm's notice board.
/// </summary>
/// <remarks>
/// Section 6's third unticked row. One row read by many, which is the opposite shape from a
/// notice — and the tests here are about the two decisions that shape forces.
///
/// The first is that reads are not recorded and acknowledgements are. There is no test that a
/// read count is absent, because a test cannot assert the absence of a feature; what is tested is
/// that the outstanding list — the one number worth putting in front of somebody — is computable
/// from the acknowledgements that do exist.
///
/// The second is that the words freeze once anybody has answered. That one has a test, because it
/// is a refusal somebody will hit and be annoyed by, and the refusal has to explain itself well
/// enough that they post a new announcement instead of looking for a way round.
/// </remarks>
public class AnnouncementTests
{
    /// <summary>
    /// Nothing is on the board until it is posted.
    /// </summary>
    /// <remarks>
    /// The draft state is here because a firm-wide announcement is the one message in this system
    /// that cannot be unsaid. Somebody writing about a redundancy or an incident wants to read it
    /// once more before everybody else does, and a system with no draft makes the first draft the
    /// published one.
    /// </remarks>
    [Fact]
    public async Task A_draft_is_not_on_anybodys_board()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var author = await Somebody(fixture, context, "Grace Wanjiru", null);

        var announcement = await service.WriteAsync(
            "The office is closed on Friday", "Madaraka Day falls on the Friday.", author);

        Assert.Empty(await service.UpForAsync(null));

        await service.PostAsync(announcement.Id);

        Assert.Single(await service.UpForAsync(null));
    }

    /// <summary>
    /// A departmental announcement is not on everybody's board.
    /// </summary>
    /// <remarks>
    /// A board that is mostly other people's business stops being read, which defeats the only
    /// thing it does. Somebody with no department sees the firm-wide ones, which is right — a
    /// contractor has no department's business to read.
    /// </remarks>
    [Fact]
    public async Task Something_addressed_to_one_department_stays_there()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var delivery = Department.Open("Delivery", "delivery");
        var office = Department.Open("Office", "office");

        context.Departments.AddRange(delivery, office);
        await context.SaveChangesAsync();

        var author = await Somebody(fixture, context, "Grace Wanjiru", office.Id);

        var everybody = await service.WriteAsync("Office closed", "On Friday.", author);
        var theirs = await service.WriteAsync(
            "Stand-up moves to 09:15", "From Monday.", author, delivery.Id);

        await service.PostAsync(everybody.Id);
        await service.PostAsync(theirs.Id);

        Assert.Equal(2, (await service.UpForAsync(delivery.Id)).Count);
        Assert.Single(await service.UpForAsync(office.Id));
        Assert.Single(await service.UpForAsync(null));
    }

    /// <summary>
    /// The outstanding list is what a person has not answered.
    /// </summary>
    /// <remarks>
    /// The one count this feature puts in front of anybody, and it is computed from
    /// acknowledgements rather than from a read receipt — which is the whole argument for
    /// recording the first and not inventing the second.
    /// </remarks>
    [Fact]
    public async Task What_is_outstanding_is_what_this_person_has_not_acknowledged()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var author = await Somebody(fixture, context, "Grace Wanjiru", null);
        var reader = await Somebody(fixture, context, "Brian Kiptoo", null);

        var worthKnowing = await service.WriteAsync("Office closed", "On Friday.", author);

        var asksForAnswer = await service.WriteAsync(
            "New expenses policy",
            "Receipts within thirty days.",
            author,
            needsAcknowledgement: true);

        await service.PostAsync(worthKnowing.Id);
        await service.PostAsync(asksForAnswer.Id);

        // Two on the board, one of them waiting on this person.
        Assert.Equal(2, (await service.UpForAsync(null)).Count);
        Assert.Single(await service.OutstandingForAsync(reader, null));

        await service.AcknowledgeAsync(asksForAnswer.Id, reader);

        Assert.Empty(await service.OutstandingForAsync(reader, null));

        // And it is still waiting on everybody else, which is the point of the list.
        Assert.Single(await service.OutstandingForAsync(author, null));
    }

    /// <summary>
    /// Acknowledging twice changes nothing.
    /// </summary>
    /// <remarks>
    /// The button is on a page people reload. A second row would make the count wrong in the
    /// direction that matters — more people would appear to have answered than have.
    /// </remarks>
    [Fact]
    public async Task Acknowledging_twice_records_one_answer()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var author = await Somebody(fixture, context, "Grace Wanjiru", null);
        var reader = await Somebody(fixture, context, "Brian Kiptoo", null);

        var announcement = await service.WriteAsync(
            "New expenses policy", "Receipts within thirty days.", author,
            needsAcknowledgement: true);

        await service.PostAsync(announcement.Id);
        await service.AcknowledgeAsync(announcement.Id, reader);
        await service.AcknowledgeAsync(announcement.Id, reader);

        Assert.Single((await service.OneAsync(announcement.Id))!.Acknowledgements);
    }

    /// <summary>
    /// Nobody can acknowledge something that does not ask for it, or is not up.
    /// </summary>
    /// <remarks>
    /// The first because it would make the announcements that <i>were</i> answered
    /// indistinguishable from the ones nobody was asked about — and the outstanding list is built
    /// on that distinction. The second because a draft is nothing the firm has said yet.
    /// </remarks>
    [Fact]
    public async Task An_acknowledgement_needs_something_that_asked_for_one_and_is_up()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var author = await Somebody(fixture, context, "Grace Wanjiru", null);
        var reader = await Somebody(fixture, context, "Brian Kiptoo", null);

        var quiet = await service.WriteAsync("Office closed", "On Friday.", author);

        await service.PostAsync(quiet.Id);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AcknowledgeAsync(quiet.Id, reader));

        Assert.Contains("does not ask to be acknowledged", refusal.Message);

        var draft = await service.WriteAsync(
            "New expenses policy", "Receipts within thirty days.", author,
            needsAcknowledgement: true);

        var second = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AcknowledgeAsync(draft.Id, reader));

        Assert.Contains("not up", second.Message);
    }

    /// <summary>
    /// Once somebody has answered, the words are fixed.
    /// </summary>
    /// <remarks>
    /// The decision in this feature most likely to be argued with, so it is tested and the
    /// refusal is tested for saying why. Somebody who acknowledged a policy agreed to what it
    /// said at the time; editing it afterwards leaves their answer on a document that has
    /// changed since, which is a worse record than none.
    ///
    /// A typo is still fixable right up until the first answer, because the alternative is a
    /// board filling with corrections of corrections.
    /// </remarks>
    [Fact]
    public async Task The_words_freeze_once_anybody_has_acknowledged_it()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var author = await Somebody(fixture, context, "Grace Wanjiru", null);
        var reader = await Somebody(fixture, context, "Brian Kiptoo", null);

        var announcement = await service.WriteAsync(
            "New expenses policy", "Receipts within thirty days.", author,
            needsAcknowledgement: true);

        await service.PostAsync(announcement.Id);

        // Fixable until the first answer.
        await service.SayAsync(
            announcement.Id, "New expenses policy", "Receipts within sixty days.", null, null);

        await service.AcknowledgeAsync(announcement.Id, reader);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SayAsync(
                announcement.Id, "New expenses policy", "Receipts within ninety days.", null,
                null));

        Assert.Contains("already said they have read this", refusal.Message);
        Assert.Contains("Post a new announcement", refusal.Message);

        Assert.Equal(
            "Receipts within sixty days.", (await service.OneAsync(announcement.Id))!.Body);
    }

    /// <summary>
    /// Something taken down leaves the board and stays on file.
    /// </summary>
    /// <remarks>
    /// An announcement that vanished leaves everybody who acted on it holding an instruction this
    /// system says was never given, and the argument that follows has no record to settle it.
    /// </remarks>
    [Fact]
    public async Task Taking_one_down_leaves_the_board_and_deletes_nothing()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var author = await Somebody(fixture, context, "Grace Wanjiru", null);

        var announcement = await service.WriteAsync(
            "Stand-up moves to 09:15", "From Monday.", author);

        await service.PostAsync(announcement.Id);
        await service.WithdrawAsync(announcement.Id, "the date was wrong");

        Assert.Empty(await service.UpForAsync(null));

        var loaded = await service.OneAsync(announcement.Id);

        Assert.Equal(AnnouncementState.Withdrawn, loaded!.State);
        Assert.Contains("the date was wrong", loaded.Outcome);
        Assert.Equal(1, await context.Announcements.CountAsync());
    }

    /// <summary>
    /// Something expired comes off the board and keeps its page.
    /// </summary>
    /// <remarks>
    /// Because a board with last year's Christmas arrangements on it is a board nobody reads —
    /// and because somebody asking in September what the rule was in March needs the page rather
    /// than an apology.
    /// </remarks>
    [Fact]
    public async Task Something_expired_is_off_the_board_and_still_readable()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var author = await Somebody(fixture, context, "Grace Wanjiru", null);

        var announcement = await service.WriteAsync(
            "Office closed on Friday", "Madaraka Day.", author);

        await service.PostAsync(announcement.Id);
        await service.SayAsync(
            announcement.Id,
            "Office closed on Friday",
            "Madaraka Day.",
            null,
            fixture.Clock.Today.AddDays(3));

        // Up today, because the day it comes off has not arrived.
        Assert.Single(await service.UpForAsync(null));

        /*
         * Time passes rather than the date being back-dated, because the domain refuses an
         * expiry earlier than the posting — which it should: something already up that has to
         * go now is withdrawn, and back-dating it would rewrite when the firm stopped saying
         * it. The first version of this test back-dated, and the refusal caught it.
         */
        fixture.Clock.Advance(TimeSpan.FromDays(4));

        Assert.Empty(await service.UpForAsync(null));

        var loaded = await service.OneAsync(announcement.Id);

        Assert.NotNull(loaded);
        Assert.True(loaded!.HasExpiredOn(fixture.Clock.Today));
        Assert.Equal(AnnouncementState.Posted, loaded.State);
    }

    /// <summary>
    /// A draft can change its mind about asking to be acknowledged. Something up cannot.
    /// </summary>
    /// <remarks>
    /// Found by writing one on the real screen with the box unticked and then looking for the way
    /// back. There was none: nothing here deletes a draft, and withdrawing only applies to
    /// something that went up, so a draft with the wrong setting was stuck being the wrong kind of
    /// announcement for ever.
    ///
    /// Fixed one way and not the other, and the asymmetry is the point. Turning it on after people
    /// have read it asks them a question that was not there when they did; turning it off throws
    /// away answers already given.
    /// </remarks>
    [Fact]
    public async Task Whether_it_asks_to_be_acknowledged_can_change_until_it_is_up()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var author = await Somebody(fixture, context, "Grace Wanjiru", null);
        var reader = await Somebody(fixture, context, "Brian Kiptoo", null);

        var announcement = await service.WriteAsync(
            "New expenses policy", "Receipts within thirty days.", author);

        Assert.False(announcement.NeedsAcknowledgement);

        await service.SayAsync(
            announcement.Id,
            "New expenses policy",
            "Receipts within thirty days.",
            null,
            null,
            needsAcknowledgement: true);

        await service.PostAsync(announcement.Id);

        // Now it means something to somebody, so it is fixed.
        await service.AcknowledgeAsync(announcement.Id, reader);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SayAsync(
                announcement.Id,
                "New expenses policy",
                "Receipts within thirty days.",
                null,
                null,
                needsAcknowledgement: false));

        // The words froze first, which is the rule that catches this — and it says so.
        Assert.Contains("already said they have read this", refusal.Message);

        Assert.True((await service.OneAsync(announcement.Id))!.NeedsAcknowledgement);

        /*
         * And the refusal the new rule itself produces, on something up that nobody has answered
         * yet — where the words are still editable and only this one setting is not. Without this
         * the test above would pass with the rule deleted, because it is the frozen words doing
         * the refusing.
         */
        var second = await service.WriteAsync("Office closed", "On Friday.", author);

        await service.PostAsync(second.Id);

        var itsOwn = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SayAsync(
                second.Id, "Office closed", "On Friday.", null, null,
                needsAcknowledgement: true));

        Assert.Contains("part of what people were shown", itsOwn.Message);
    }

    /// <summary>An announcement has to say something.</summary>
    [Fact]
    public async Task An_announcement_cannot_be_blank()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var author = await Somebody(fixture, context, "Grace Wanjiru", null);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.WriteAsync("   ", "Something.", author));

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.WriteAsync("Something", "   ", author));
    }

    private static AnnouncementService Service(DatabaseFixture fixture, TestDbContext context) =>
        new(new AnnouncementRepository(context), fixture.Clock);

    private static async Task<Guid> Somebody(
        DatabaseFixture fixture, TestDbContext context, string name, Guid? departmentId)
    {
        var employee = Employee.Hire(name, fixture.Clock.Today, departmentId, "Engineer");

        context.Employees.Add(employee);
        await context.SaveChangesAsync();

        return employee.Id;
    }
}
