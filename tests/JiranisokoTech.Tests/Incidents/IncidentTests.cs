using JiranisokoTech.Application.Incidents;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Domain.Incidents;
using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.Incidents;
using JiranisokoTech.Infrastructure.Work;
using JiranisokoTech.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Tests.Incidents;

/// <summary>
/// Running an incident, and what the record of it is allowed to say.
/// </summary>
/// <remarks>
/// Sections 27 and 69, and the brief's fourth critical workflow — the only one of the four that
/// had nothing at all.
///
/// Four things in here are worth more than the rest.
///
/// <b>Three timestamps that mean three things.</b> When it started, when somebody noticed, when
/// the harm stopped. Nearly everything anybody wants to know afterwards is a subtraction between
/// two of them, and a system with one "created" column can answer none of those questions —
/// including the one people most want, which is how long it was broken before anybody knew.
///
/// <b>Mitigated is not resolved.</b> A flag turned off at 02:14 stopped the harm; the bug was
/// still there the next morning. Collapsing the two makes every duration afterwards answer a
/// question nobody asked.
///
/// <b>The timeline is append-only and records its own changes.</b> Severity moves, start
/// corrections and resolutions write their own lines, because a timeline assembled later from
/// memory is the artefact of an incident that is always missing.
///
/// <b>A corrective action is a real work item.</b> The whole difference between a review that
/// changes something and a folder of documents.
/// </remarks>
public class IncidentTests
{
    private static readonly Guid Engineer = Guid.CreateVersion7();

    /// <summary>
    /// Reporting records the gap between when it started and when anybody knew.
    /// </summary>
    /// <remarks>
    /// And says it in words on the first timeline line, at the moment somebody is best placed to
    /// correct it. A number in a column marked "time to detect" is looked at once a quarter; a
    /// sentence saying "already going for about 40 minutes" is read by whoever is running the
    /// incident.
    /// </remarks>
    [Fact]
    public async Task An_incident_records_how_long_it_ran_before_anybody_noticed()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var incident = await service.ReportAsync(
            "Card payments are failing",
            IncidentSeverity.Critical,
            fixture.Clock.Now.AddMinutes(-40),
            Engineer);

        Assert.Equal(TimeSpan.FromMinutes(40), incident.ToDetect);
        Assert.Contains(
            "Already going for about 40 minutes",
            incident.Notes.Single().Text);
    }

    /// <summary>
    /// The first line counts in words, and counts one of something as one.
    /// </summary>
    /// <remarks>
    /// It said "about 1 hours" on the live screen, which is the sort of thing that makes a
    /// reader distrust everything else on the page — and this line is quoted into the review
    /// afterwards. Found by opening the page, not by any test, which is why there is now one.
    /// </remarks>
    [Theory]
    [InlineData(1, "Noticed as it began")]
    [InlineData(5, "about 5 minutes")]
    [InlineData(40, "about 40 minutes")]
    [InlineData(60, "about one hour")]
    [InlineData(150, "about 3 hours")]
    [InlineData(1_440, "about one day")]
    [InlineData(4_320, "about 3 days")]
    public async Task The_first_line_counts_in_words(int minutes, string expected)
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var incident = await service.ReportAsync(
            "Something",
            IncidentSeverity.Minor,
            fixture.Clock.Now.AddMinutes(-minutes),
            Engineer);

        Assert.Contains(expected, incident.Notes.Single().Text);
    }

    /// <summary>An incident cannot have started in the future.</summary>
    [Fact]
    public async Task An_incident_cannot_have_started_in_the_future()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.ReportAsync(
                "Tomorrow's outage",
                IncidentSeverity.Minor,
                fixture.Clock.Now.AddHours(1),
                Engineer));
    }

    /// <summary>
    /// A severity that moves says so in the timeline, with both values and the reason.
    /// </summary>
    /// <remarks>
    /// The single most useful sentence in most reviews is "we thought it was minor for the first
    /// forty minutes", and a system that stores only the final severity deletes it.
    /// </remarks>
    [Fact]
    public async Task Changing_the_severity_writes_a_line_naming_both()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var incident = await service.ReportAsync(
            "Payments slow", IncidentSeverity.Minor, fixture.Clock.Now, Engineer);

        await service.ReclassifyAsync(
            incident.Id, IncidentSeverity.Critical, "it is every payment, not some", Engineer);

        var line = incident.Notes.Last();

        Assert.Contains("Minor to Critical", line.Text);
        Assert.Contains("it is every payment", line.Text);
    }

    [Fact]
    public async Task A_severity_cannot_move_without_a_reason()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var incident = await service.ReportAsync(
            "Payments slow", IncidentSeverity.Minor, fixture.Clock.Now, Engineer);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.ReclassifyAsync(
                incident.Id, IncidentSeverity.Critical, "  ", Engineer));
    }

    /// <summary>
    /// Stopping the harm and removing the cause are two different moments.
    /// </summary>
    /// <remarks>
    /// The reason IncidentStatus has a middle. Both durations are asserted because collapsing
    /// them is the mistake, and a test that only checked the second one would pass against a
    /// model that had thrown the first away.
    /// </remarks>
    [Fact]
    public async Task Mitigating_and_resolving_are_two_moments()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var started = fixture.Clock.Now;

        var incident = await service.ReportAsync(
            "Prices are wrong", IncidentSeverity.Critical, started, Engineer);

        fixture.Clock.Advance(TimeSpan.FromMinutes(20));

        await service.MitigateAsync(
            incident.Id, "turned the new pricing flag off", fixture.Clock.Now, Engineer);

        Assert.Equal(IncidentStatus.Mitigated, incident.Status);

        fixture.Clock.Advance(TimeSpan.FromHours(11));

        await service.ResolveAsync(
            incident.Id,
            "the cache was never invalidated after a price change",
            fixture.Clock.Now,
            Engineer);

        Assert.Equal(TimeSpan.FromMinutes(20), incident.ToMitigate);
        Assert.Equal(TimeSpan.FromMinutes(20) + TimeSpan.FromHours(11), incident.ToResolve);
    }

    /// <summary>
    /// An incident fixed rather than worked around sets both moments to the same one.
    /// </summary>
    /// <remarks>
    /// Because plenty of incidents are simply fixed, and forcing a mitigation step first would
    /// have people clicking a button to describe something they did not do. Recording the same
    /// moment twice is true: the harm stopped when the cause did.
    /// </remarks>
    [Fact]
    public async Task Resolving_without_mitigating_first_sets_both()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var incident = await service.ReportAsync(
            "Wrong logo on the statement", IncidentSeverity.Minor, fixture.Clock.Now, Engineer);

        fixture.Clock.Advance(TimeSpan.FromMinutes(5));

        await service.ResolveAsync(
            incident.Id, "the wrong asset was deployed", fixture.Clock.Now, Engineer);

        Assert.Equal(incident.ResolvedAt, incident.MitigatedAt);
        Assert.Equal(TimeSpan.FromMinutes(5), incident.ToResolve);
    }

    /// <summary>
    /// Reopening clears what was not true and keeps every line that said it was.
    /// </summary>
    /// <remarks>
    /// The same incident rather than a new one: same start, same customers, same cause. Two
    /// records would split the timeline in half and make both durations wrong.
    /// </remarks>
    [Fact]
    public async Task Reopening_clears_the_endings_and_keeps_the_timeline()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var incident = await service.ReportAsync(
            "Queue backing up", IncidentSeverity.Major, fixture.Clock.Now, Engineer);

        await service.ResolveAsync(
            incident.Id, "a consumer was wedged", fixture.Clock.Now, Engineer);

        var before = incident.Notes.Count;

        fixture.Clock.Advance(TimeSpan.FromHours(2));

        await service.ReopenAsync(incident.Id, "it started again at 04:10", Engineer);

        Assert.Equal(IncidentStatus.Open, incident.Status);
        Assert.Null(incident.MitigatedAt);
        Assert.Null(incident.ResolvedAt);
        Assert.Equal(before + 1, incident.Notes.Count);
        Assert.Contains(incident.Notes, one => one.Text.StartsWith("Resolved:"));
    }

    /// <summary>
    /// The start can be corrected backwards and not past the report.
    /// </summary>
    /// <remarks>
    /// Both halves. The correction is the normal case — nobody knows when it started until
    /// somebody has looked at a graph — and the refusal stops a typo making time-to-detect
    /// negative, which would quietly poison every average computed from it.
    /// </remarks>
    [Fact]
    public async Task The_start_can_be_corrected_but_not_past_the_report()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var incident = await service.ReportAsync(
            "Positions are stale", IncidentSeverity.Major, fixture.Clock.Now, Engineer);

        await service.StartedAtAsync(
            incident.Id, fixture.Clock.Now.AddHours(-3), Engineer);

        Assert.Equal(TimeSpan.FromHours(3), incident.ToDetect);
        Assert.Contains(incident.Notes, one => one.Text.StartsWith("Start moved"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.StartedAtAsync(
                incident.Id, fixture.Clock.Now.AddHours(1), Engineer));
    }

    /// <summary>
    /// A line written afterwards sits at the time it is about, and says it was written later.
    /// </summary>
    /// <remarks>
    /// Both, because either alone is wrong. Filing it at the moment it was typed makes the
    /// timeline unreadable; filing it at the time it describes without saying so makes a
    /// reconstruction look contemporaneous, which is the exact mistake a review is prone to.
    /// </remarks>
    [Fact]
    public async Task A_line_written_later_sits_where_it_belongs_and_says_so()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var started = fixture.Clock.Now;

        var incident = await service.ReportAsync(
            "Queue backing up", IncidentSeverity.Major, started, Engineer);

        fixture.Clock.Advance(TimeSpan.FromMinutes(40));

        await service.NoteAsync(
            incident.Id,
            "the queue was already deep at the top of the hour",
            NoteKind.Observation,
            started.AddMinutes(-58),
            Engineer);

        var earliest = incident.Notes.OrderBy(one => one.At).First();

        Assert.StartsWith("the queue was already deep", earliest.Text);
        Assert.True(earliest.WrittenLater);
    }

    /// <summary>Two incidents cannot share a number.</summary>
    /// <remarks>
    /// The number is what people say out loud and write in messages, and two incidents called 14
    /// make every one of those references ambiguous — including the ones already sent. The
    /// sequence comes from a read of the maximum, so the index is what stops two raised in the
    /// same second from taking it, which is precisely when two get raised.
    /// </remarks>
    [Fact]
    public async Task Two_incidents_cannot_share_a_number()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();

        context.Incidents.Add(Incident.Report(
            4, "One", IncidentSeverity.Minor, fixture.Clock.Now, fixture.Clock.Now, Engineer));

        context.Incidents.Add(Incident.Report(
            4, "Two", IncidentSeverity.Minor, fixture.Clock.Now, fixture.Clock.Now, Engineer));

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    /// <summary>
    /// A review cannot be written before the incident is over.
    /// </summary>
    /// <remarks>
    /// Because a review written while the cause is still unknown records a guess, and a guess in
    /// a document headed "why it was possible" is believed for years.
    /// </remarks>
    [Fact]
    public async Task A_review_cannot_start_before_the_incident_is_resolved()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var incident = await service.ReportAsync(
            "Payments failing", IncidentSeverity.Critical, fixture.Clock.Now, Engineer);

        await service.MitigateAsync(
            incident.Id, "failed over to the other provider", fixture.Clock.Now, Engineer);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ReviewAsync(incident.Id, Engineer));
    }

    /// <summary>Opening the review twice gives back the same one.</summary>
    /// <remarks>
    /// Two people opening the review page at once is the ordinary case, and two reviews of one
    /// incident are two accounts of the same event with no way to tell which the firm meant.
    /// </remarks>
    [Fact]
    public async Task A_second_review_of_one_incident_is_the_first_one()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var incident = await Resolved(service, fixture);

        var first = await service.ReviewAsync(incident.Id, Engineer);
        var again = await service.ReviewAsync(incident.Id, Engineer);

        Assert.Equal(first.Id, again.Id);
        Assert.Equal(1, await context.Postmortems.CountAsync());
    }

    /// <summary>
    /// A review cannot be agreed while a question is unanswered.
    /// </summary>
    /// <remarks>
    /// The one people leave out is how it was noticed, which is usually the one worth the most:
    /// an incident found by a customer telephoning is a different firm from one found by an
    /// alert, and the gap between the two is normally the cheapest thing on the list to fix.
    /// </remarks>
    [Fact]
    public async Task An_unanswered_review_cannot_be_agreed()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var incident = await Resolved(service, fixture);

        await service.ReviewAsync(incident.Id, Engineer);

        await service.WriteReviewAsync(
            incident.Id, "The prices were wrong.", "A cache was not invalidated.", "", "");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AgreeReviewAsync(incident.Id, Engineer));
    }

    /// <summary>
    /// A review with no actions has to say why there is nothing to do.
    /// </summary>
    /// <remarks>
    /// Both halves asserted. "Nothing to do" is a real answer — an outage at somebody else's
    /// provider may need nothing from us — but a review that can be agreed with an empty list
    /// and no explanation is one that gets agreed in a meeting that ran over, and the section of
    /// the system meant to make the firm learn something becomes a folder of blank documents.
    /// </remarks>
    [Fact]
    public async Task A_review_with_nothing_to_do_has_to_say_why()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var incident = await Resolved(service, fixture);

        await service.ReviewAsync(incident.Id, Engineer);
        await Answer(service, incident.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AgreeReviewAsync(incident.Id, Engineer));

        await service.AgreeReviewAsync(
            incident.Id, Engineer, "the outage was at the payment provider");

        var review = await context.Postmortems.SingleAsync();

        Assert.True(review.IsAgreed);
        Assert.Equal("the outage was at the payment provider", review.NothingToDoBecause);
    }

    /// <summary>
    /// A corrective action is a real work item on the real board.
    /// </summary>
    /// <remarks>
    /// The decision the whole of section 69 turns on. An action kept inside a review is not on
    /// the board, not in anybody's week, and first read during the next incident — which is when
    /// somebody notices the same action was agreed last time. This asserts the board item
    /// exists, that it is raised high, and that it says where it came from, because a task
    /// somebody picks up in six weeks is worth nothing without the reason.
    /// </remarks>
    [Fact]
    public async Task An_agreed_action_lands_on_the_board_saying_where_it_came_from()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var incident = await Resolved(service, fixture);

        await service.ReviewAsync(incident.Id, Engineer);
        await Answer(service, incident.Id);

        var action = await service.ActAsync(
            incident.Id, "alert when the queue is deeper than a thousand", Engineer);

        var item = await context.WorkItems.SingleAsync(one => one.Id == action.WorkItemId);

        Assert.Equal("alert when the queue is deeper than a thousand", item.Title);
        Assert.Equal(Priority.High, item.Priority);
        Assert.Contains($"review of incident {incident.Number}", item.Detail);
        Assert.Equal(item.Number, action.Number);
    }

    /// <summary>
    /// Taking an action off a review leaves the board item alone.
    /// </summary>
    /// <remarks>
    /// Deleting it would be one aggregate reaching into another to destroy something a person
    /// may have started. The honest result is an action off the review and a task on the board
    /// that somebody can close themselves.
    /// </remarks>
    [Fact]
    public async Task Dropping_an_action_leaves_the_work_item_alone()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var incident = await Resolved(service, fixture);

        await service.ReviewAsync(incident.Id, Engineer);
        await Answer(service, incident.Id);

        var action = await service.ActAsync(incident.Id, "add the alert", Engineer);

        await service.DropActionAsync(incident.Id, action.Id);

        var review = await context.Postmortems
            .Include(one => one.Actions)
            .SingleAsync();

        Assert.Empty(review.Actions);
        Assert.True(await context.WorkItems.AnyAsync(one => one.Id == action.WorkItemId));
    }

    /// <summary>
    /// An agreed review cannot be quietly rewritten.
    /// </summary>
    [Fact]
    public async Task An_agreed_review_is_closed_until_it_is_reopened()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var incident = await Resolved(service, fixture);

        await service.ReviewAsync(incident.Id, Engineer);
        await Answer(service, incident.Id);
        await service.ActAsync(incident.Id, "add the alert", Engineer);
        await service.AgreeReviewAsync(incident.Id, Engineer);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.WriteReviewAsync(incident.Id, "Something else", "x", "y", "z"));

        await service.ReopenReviewAsync(incident.Id);
        await service.WriteReviewAsync(incident.Id, "Something else", "x", "y", "z");

        var review = await context.Postmortems.SingleAsync();

        Assert.Equal("Something else", review.WhatHappened);
        Assert.False(review.IsAgreed);
    }

    /// <summary>
    /// What changed before it started is a suspect; what changed after is not.
    /// </summary>
    /// <remarks>
    /// The first question in every incident, answered without anybody typing. The upper bound is
    /// the start rather than the report, because a deployment that happened after the thing was
    /// already broken did not break it — though it is exactly the coincidence people convince
    /// themselves of at two in the morning.
    /// </remarks>
    [Fact]
    public async Task What_went_out_before_it_started_is_listed_and_what_went_out_after_is_not()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var started = fixture.Clock.Now;

        await using var context = fixture.NewContext();

        var repository = JiranisokoTech.Domain.Engineering.Repository.Connect(
            GitProvider.GitHub, "jiranisoko", "erp", null, "hash", started.AddDays(-30));

        context.Repositories.Add(repository);

        context.Deployments.Add(Deployment.Record(
            repository.Id,
            "before",
            "production",
            "1a11111111111111111111111111111111111111",
            state: DeploymentState.Succeeded,
            at: started.AddMinutes(-25)));

        context.Deployments.Add(Deployment.Record(
            repository.Id,
            "after",
            "production",
            "2b22222222222222222222222222222222222222",
            state: DeploymentState.Succeeded,
            at: started.AddMinutes(10)));

        await context.SaveChangesAsync();

        var suspects = await new IncidentQueries(context)
            .SuspectsAsync(started, TimeSpan.FromHours(4));

        Assert.Single(suspects);
        Assert.Equal("1a11111111111111111111111111111111111111", suspects[0].Sha);
    }

    private static IncidentService Service(DatabaseFixture fixture, TestDbContext context) =>
        new(new IncidentRepository(context), new WorkRepository(context), fixture.Clock);

    private static async Task<Incident> Resolved(
        IncidentService service, DatabaseFixture fixture)
    {
        var incident = await service.ReportAsync(
            "Prices are wrong", IncidentSeverity.Critical, fixture.Clock.Now, Engineer);

        await service.ResolveAsync(
            incident.Id,
            "the cache was never invalidated after a price change",
            fixture.Clock.Now,
            Engineer);

        return incident;
    }

    private static Task Answer(IncidentService service, Guid incidentId) =>
        service.WriteReviewAsync(
            incidentId,
            "Prices shown to customers were the previous day's for about twenty minutes.",
            "Nothing invalidated the price cache when a price changed.",
            "A customer telephoned.",
            "An alert comparing the cached price to the stored one.");
}
