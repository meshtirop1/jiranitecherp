using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Application.People;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Performance;
using JiranisokoTech.Infrastructure.Authorization;
using JiranisokoTech.Infrastructure.People;
using JiranisokoTech.Tests.Infrastructure;

namespace JiranisokoTech.Tests.People;

/// <summary>
/// Goals, and the review cycles they are discussed in.
/// </summary>
/// <remarks>
/// Section 6's second unticked row. Three of the tests here are about rules that exist to prevent
/// a specific harm rather than a specific error, which is unusual in this suite and worth saying
/// out loud:
///
/// <b>Nobody reads a half-written appraisal of themselves.</b> It does not take malice — a manager
/// saving a draft on Friday to finish on Monday is enough — and it is the worst thing this feature
/// could do, so the rule is in the aggregate behind a single accessor rather than on a screen.
///
/// <b>Nobody writes somebody else's self-assessment.</b> A self-assessment a manager can edit is a
/// form somebody filled in on your behalf.
///
/// <b>A cycle cannot close over an unshared verdict.</b> That would leave a manager's written
/// opinion of somebody in a system that person can sign into, about a conversation that never
/// happened.
/// </remarks>
public class PerformanceTests
{
    /// <summary>
    /// A goal has to say how it will be judged.
    /// </summary>
    /// <remarks>
    /// The rule here that does the real work. "Improve communication" is not a goal; it is a mood,
    /// and at review time it can be argued either way by whoever is more confident. Requiring the
    /// measure moves that argument to the moment the goal is set, which is when it is cheap and
    /// when the person holding it is in the room.
    /// </remarks>
    [Fact]
    public async Task A_goal_needs_a_measure_and_cannot_end_before_it_starts()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var person = await Somebody(fixture, context, "Brian Kiptoo", null);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.SetGoalAsync(
                person, person, "Improve communication", "   ",
                fixture.Clock.Today, fixture.Clock.Today.AddMonths(6)));

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.SetGoalAsync(
                person, person, "Run the rota", "The rota is written down",
                fixture.Clock.Today, fixture.Clock.Today.AddDays(-1)));
    }

    /// <summary>
    /// A closed goal is closed, and reopening is how a mis-press is undone.
    /// </summary>
    /// <remarks>
    /// Editing a closed goal would change what somebody was judged on after the judging. Reopening
    /// is allowed because a goal is a working agreement rather than a record of what was known at
    /// a moment, and without it the only fix for a wrong button is a second goal with the same
    /// words — which reads as though somebody was set the same thing twice.
    /// </remarks>
    [Fact]
    public async Task A_closed_goal_refuses_edits_until_it_is_reopened()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var person = await Somebody(fixture, context, "Brian Kiptoo", null);

        var goal = await service.SetGoalAsync(
            person, person, "Run the rota", "The rota is written down",
            fixture.Clock.Today, fixture.Clock.Today.AddMonths(6));

        await service.CloseGoalAsync(goal.Id, GoalOutcome.Partly, "The rota exists, nobody else has deployed.");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.NoteGoalAsync(goal.Id, person, "One more thing."));

        Assert.Contains("Reopen it", refusal.Message);

        await service.ReopenGoalAsync(goal.Id);
        await service.NoteGoalAsync(goal.Id, person, "One more thing.");

        var loaded = await service.GoalAsync(goal.Id);

        Assert.True(loaded!.IsOpen);
        Assert.Single(loaded.Notes);
        Assert.Null(loaded.Verdict);
    }

    /// <summary>Closing a goal needs a sentence, not only an outcome.</summary>
    [Fact]
    public async Task Closing_a_goal_needs_words()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var person = await Somebody(fixture, context, "Brian Kiptoo", null);

        var goal = await service.SetGoalAsync(
            person, person, "Run the rota", "The rota is written down",
            fixture.Clock.Today, fixture.Clock.Today.AddMonths(6));

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.CloseGoalAsync(goal.Id, GoalOutcome.Partly, "   "));
    }

    /// <summary>
    /// Opening a cycle covers everybody who works here.
    /// </summary>
    /// <remarks>
    /// Rather than letting reviews appear as people start writing. A list that fills up as people
    /// participate cannot show who has not, which is the only thing anybody wants from it.
    ///
    /// The person who has not started is left out and the leaver is left out; the person at the
    /// top is included with no manager, because leaving them out would make the cycle quietly
    /// incomplete.
    /// </remarks>
    [Fact]
    public async Task Opening_a_cycle_includes_everybody_who_works_here()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var boss = await Somebody(fixture, context, "Grace Wanjiru", null);
        var engineer = await Somebody(fixture, context, "Brian Kiptoo", boss);

        // Invited and not started, so not in the cycle.
        var joiner = Employee.Hire("Faith Njeri", fixture.Clock.Today.AddMonths(1), null, "Engineer");

        context.Employees.Add(joiner);
        await context.SaveChangesAsync();

        var cycle = await service.OpenCycleAsync(
            "2026, mid-year", new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30));

        var loaded = await service.CycleAsync(cycle.Id);

        Assert.Equal(2, loaded!.Reviews.Count);
        Assert.Null(loaded.For(boss)!.ManagerEmployeeId);
        Assert.Equal(boss, loaded.For(engineer)!.ManagerEmployeeId);
        Assert.Null(loaded.For(joiner.Id));
    }

    /// <summary>
    /// The manager's half is invisible to its subject until it is shared.
    /// </summary>
    /// <remarks>
    /// The most important test in this file. The accessor is the only way to the text, so a page
    /// cannot reach round it, and the subject gets null until the moment it is shared.
    /// </remarks>
    [Fact]
    public async Task The_managers_half_is_hidden_from_its_subject_until_it_is_shared()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var boss = await Somebody(fixture, context, "Grace Wanjiru", null);
        var engineer = await Somebody(fixture, context, "Brian Kiptoo", boss);

        var cycle = await service.OpenCycleAsync(
            "2026, mid-year", new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30));

        await service.WriteManagerAsync(
            cycle.Id, engineer, boss, "A strong year on the platform.", ReviewRating.Exceeding);

        var before = (await service.CycleAsync(cycle.Id))!.For(engineer)!;

        Assert.True(before.HasManagerNote);
        Assert.Null(before.ManagerNoteFor(engineer));
        Assert.Null(before.RatingFor(engineer));

        // Their manager can read what they wrote, which is how they finish it.
        Assert.Equal("A strong year on the platform.", before.ManagerNoteFor(boss));
        Assert.Equal(ReviewRating.Exceeding, before.RatingFor(boss));

        await service.ShareAsync(cycle.Id, engineer);

        var after = (await service.CycleAsync(cycle.Id))!.For(engineer)!;

        Assert.Equal("A strong year on the platform.", after.ManagerNoteFor(engineer));
        Assert.Equal(ReviewRating.Exceeding, after.RatingFor(engineer));
    }

    /// <summary>Nobody writes somebody else's half, in either direction.</summary>
    [Fact]
    public async Task Neither_half_can_be_written_by_the_wrong_person()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var boss = await Somebody(fixture, context, "Grace Wanjiru", null);
        var engineer = await Somebody(fixture, context, "Brian Kiptoo", boss);

        var cycle = await service.OpenCycleAsync(
            "2026, mid-year", new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30));

        // The service takes the person whose review it is, so a manager cannot pass themselves in
        // as the author of somebody else's self-assessment — the aggregate refuses it outright.
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.WriteManagerAsync(
                cycle.Id, engineer, engineer, "I had a good year.", ReviewRating.Exceeding));

        Assert.Contains("own review", refusal.Message);
    }

    /// <summary>Sharing an empty review is refused.</summary>
    /// <remarks>
    /// It would tell somebody their appraisal is ready and then show them nothing, which is worse
    /// than silence: they will read the blank as a verdict.
    /// </remarks>
    [Fact]
    public async Task Nothing_is_shared_before_it_is_written()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var boss = await Somebody(fixture, context, "Grace Wanjiru", null);
        var engineer = await Somebody(fixture, context, "Brian Kiptoo", boss);

        var cycle = await service.OpenCycleAsync(
            "2026, mid-year", new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30));

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ShareAsync(cycle.Id, engineer));

        Assert.Contains("nothing to share", refusal.Message);
    }

    /// <summary>
    /// A cycle will not close over a verdict nobody was shown.
    /// </summary>
    /// <remarks>
    /// The refusal that justifies the cycle existing. Without it the natural end of a review round
    /// is somebody pressing "close" on a list where three people's appraisals were written and
    /// never discussed, and the system would then present that as finished work.
    /// </remarks>
    [Fact]
    public async Task A_cycle_refuses_to_close_over_an_unshared_review()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var boss = await Somebody(fixture, context, "Grace Wanjiru", null);
        var engineer = await Somebody(fixture, context, "Brian Kiptoo", boss);

        var cycle = await service.OpenCycleAsync(
            "2026, mid-year", new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30));

        await service.WriteManagerAsync(
            cycle.Id, engineer, boss, "A strong year.", ReviewRating.Meeting);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CloseCycleAsync(cycle.Id));

        Assert.Contains("never shown", refusal.Message);

        await service.ShareAsync(cycle.Id, engineer);
        await service.CloseCycleAsync(cycle.Id);

        Assert.True((await service.CycleAsync(cycle.Id))!.IsClosed);
    }

    /// <summary>
    /// Somebody's account of their own year is not removed from a cycle.
    /// </summary>
    /// <remarks>
    /// Excluding exists for the person who left in the first week, where a review is a form nobody
    /// will fill in and it sits on the chase list for ever. Once anything is written it is
    /// somebody's own words, and removing it would delete them.
    /// </remarks>
    [Fact]
    public async Task Somebody_who_has_written_something_stays_in_the_cycle()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var boss = await Somebody(fixture, context, "Grace Wanjiru", null);
        var engineer = await Somebody(fixture, context, "Brian Kiptoo", boss);

        var cycle = await service.OpenCycleAsync(
            "2026, mid-year", new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30));

        // Nothing written, so out they come.
        await service.ExcludeAsync(cycle.Id, boss);

        Assert.Null((await service.CycleAsync(cycle.Id))!.For(boss));

        await service.WriteSelfAsync(cycle.Id, engineer, "I ran the platform rota.");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ExcludeAsync(cycle.Id, engineer));

        Assert.Contains("own year", refusal.Message);
    }

    /// <summary>
    /// A closed cycle takes nothing more.
    /// </summary>
    /// <remarks>
    /// Because a review written after the round was declared finished is one nobody is looking at
    /// the list for any more, and it would sit there unshared.
    /// </remarks>
    [Fact]
    public async Task A_closed_cycle_refuses_writing()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var engineer = await Somebody(fixture, context, "Brian Kiptoo", null);

        var cycle = await service.OpenCycleAsync(
            "2026, mid-year", new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30));

        await service.CloseCycleAsync(cycle.Id);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.WriteSelfAsync(cycle.Id, engineer, "Something."));

        Assert.Contains("is closed", refusal.Message);
    }

    /// <summary>
    /// Performance follows the reporting line, not the department.
    /// </summary>
    /// <remarks>
    /// The deliberate disagreement with the roster, and the one worth a test because the two rules
    /// are next to each other in the same class and it would be easy to "fix" one to match the
    /// other. A head of engineering who is not in somebody's chain has no business in their
    /// appraisal, even if they sit in the same department.
    ///
    /// The whole chain downward, so a head with team leads under them reaches everybody below
    /// those leads.
    /// </remarks>
    [Fact]
    public async Task Whose_performance_somebody_may_read_follows_the_reporting_line()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var reaches = new Reaches(context);

        var delivery = Department.Open("Delivery", "delivery");

        context.Departments.Add(delivery);
        await context.SaveChangesAsync();

        var head = await Somebody(fixture, context, "Grace Wanjiru", null, delivery.Id);
        var lead = await Somebody(fixture, context, "Brian Kiptoo", head, delivery.Id);
        var engineer = await Somebody(fixture, context, "Faith Njeri", lead, delivery.Id);

        // In the same department and in nobody's chain.
        var other = await Somebody(fixture, context, "Peter Otieno", null, delivery.Id);

        var manages = new HashSet<string> { Permissions.GoalsManage };

        var forLead = await reaches.PerformanceAsync(manages, lead);

        Assert.Contains(lead, forLead);
        Assert.Contains(engineer, forLead);
        Assert.DoesNotContain(head, forLead);
        Assert.DoesNotContain(other, forLead);

        // Two levels down, which is the reason the chain is walked rather than one level read.
        var forHead = await reaches.PerformanceAsync(manages, head);

        Assert.Contains(engineer, forHead);
        Assert.DoesNotContain(other, forHead);

        // Without goals.manage, their own and nothing else — even for somebody with reports.
        var forReader = await reaches.PerformanceAsync(
            new HashSet<string> { Permissions.GoalsView }, lead);

        Assert.Equal([lead], forReader);

        // HR reads the firm.
        var forHr = await reaches.PerformanceAsync(
            new HashSet<string> { Permissions.GoalsViewAll }, null);

        Assert.Contains(other, forHr);
    }

    private static PerformanceService Service(DatabaseFixture fixture, TestDbContext context) =>
        new(new PerformanceRepository(context), new PeopleRepository(context), fixture.Clock);

    /// <summary>
    /// Somebody who works here, started, optionally reporting to another.
    /// </summary>
    /// <remarks>
    /// Started, because a cycle covers people who are here — and a fixture that left everybody
    /// Invited would make every cycle empty and every test pass for the wrong reason.
    /// </remarks>
    private static async Task<Guid> Somebody(
        DatabaseFixture fixture,
        TestDbContext context,
        string name,
        Guid? reportsTo,
        Guid? departmentId = null)
    {
        var employee = Employee.Hire(name, fixture.Clock.Today, departmentId, "Engineer");

        employee.Start();

        if (reportsTo is { } manager)
        {
            employee.ReportsTo(manager);
        }

        context.Employees.Add(employee);
        await context.SaveChangesAsync();

        return employee.Id;
    }
}
