using JiranisokoTech.Application.Assets;
using JiranisokoTech.Application.People;
using JiranisokoTech.Domain.Assets;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Infrastructure.Assets;
using JiranisokoTech.Infrastructure.People;
using JiranisokoTech.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Tests.Recruitment;

/// <summary>
/// The checklist for somebody starting, and the loop it closes.
/// </summary>
/// <remarks>
/// Section 8's second half, deliberately built as the mirror of section 9's offboarding. What a
/// firm needs from both is the same thing — a list of what has not happened yet — and the two
/// were never connected, so what was handed over on the first day and what was asked back on the
/// last were two separate acts of memory.
///
/// The test worth reading is the last one. Equipment issued here is carried into the leaver's
/// checklist with its serial number, which is the difference between asking for "the laptop" and
/// asking for one the firm can identify.
/// </remarks>
public class OnboardingTests
{
    private static readonly Guid Manager = Guid.CreateVersion7();

    /// <summary>A checklist starts with the usual list on it.</summary>
    /// <remarks>
    /// The default list matters more than it looks: an empty checklist is one somebody fills in
    /// from memory, which is the thing this section exists to replace.
    /// </remarks>
    [Fact]
    public async Task A_new_checklist_has_the_usual_things_on_it()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = new OnboardingService(new PeopleRepository(context), fixture.Clock);

        var employee = await Joined(fixture, context);

        var onboarding = await service.BeginAsync(employee, fixture.Clock.Today.AddDays(14));

        Assert.Equal(Onboarding.Usual.Count, onboarding.Steps.Count);
        Assert.Equal(Onboarding.Usual[0], onboarding.Steps[0].Name);
        Assert.Equal(onboarding.Steps.Count, onboarding.Outstanding.Count);
    }

    /// <summary>Starting one twice gives back the first.</summary>
    /// <remarks>
    /// Two checklists for one person means the laptop is on one and the sign-in on the other,
    /// and whoever looks sees a short list and believes it.
    /// </remarks>
    [Fact]
    public async Task A_second_checklist_is_the_first_one()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = new OnboardingService(new PeopleRepository(context), fixture.Clock);

        var employee = await Joined(fixture, context);

        var first = await service.BeginAsync(employee, fixture.Clock.Today.AddDays(14));
        var again = await service.BeginAsync(employee, fixture.Clock.Today.AddDays(21));

        Assert.Equal(first.Id, again.Id);
        Assert.Equal(1, await context.Onboardings.CountAsync());
    }

    /// <summary>
    /// It cannot be closed while anything is outstanding.
    /// </summary>
    /// <remarks>
    /// The opposite of offboarding, which allows it: a leaver's laptop sometimes never comes
    /// back, and a list that cannot be closed is one that stays open forever accusing somebody.
    /// Here the outstanding items are things the firm has not done for a person who works here,
    /// and there is no equivalent of "they kept it".
    ///
    /// Taking a step off is the way out, rather than ticking it — a list of ticks where some
    /// mean done and some mean irrelevant is a list nobody can read.
    /// </remarks>
    [Fact]
    public async Task A_checklist_closes_only_when_nothing_is_left()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = new OnboardingService(new PeopleRepository(context), fixture.Clock);

        var employee = await Joined(fixture, context);
        var onboarding = await service.BeginAsync(employee, fixture.Clock.Today.AddDays(14));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CompleteAsync(employee));

        foreach (var step in onboarding.Steps)
        {
            await service.DidAsync(employee, step.Id, Manager);
        }

        await service.CompleteAsync(employee);

        Assert.True(onboarding.IsComplete);
    }

    /// <summary>A step that does not apply comes off rather than being ticked.</summary>
    [Fact]
    public async Task A_step_that_does_not_apply_can_be_taken_off()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = new OnboardingService(new PeopleRepository(context), fixture.Clock);

        var employee = await Joined(fixture, context);
        var onboarding = await service.BeginAsync(employee, fixture.Clock.Today.AddDays(14));

        var before = onboarding.Steps.Count;

        await service.NotNeededAsync(employee, onboarding.Steps[0].Id);

        Assert.Equal(before - 1, onboarding.Steps.Count);
    }

    /// <summary>A tick can be undone, because people tick the wrong row.</summary>
    /// <remarks>
    /// Allowed, unlike an incident's timeline line. A checklist is a working document about what
    /// still has to happen rather than a record of what was known when, and a tick that cannot
    /// be undone means the list stops being true the first time somebody misses.
    /// </remarks>
    [Fact]
    public async Task A_tick_can_be_undone()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = new OnboardingService(new PeopleRepository(context), fixture.Clock);

        var employee = await Joined(fixture, context);
        var onboarding = await service.BeginAsync(employee, fixture.Clock.Today.AddDays(14));

        var step = onboarding.Steps[0].Id;

        await service.DidAsync(employee, step, Manager);

        Assert.True(onboarding.Steps.Single(one => one.Id == step).IsDone);

        await service.NotDoneAsync(employee, step);

        Assert.False(onboarding.Steps.Single(one => one.Id == step).IsDone);
    }

    /// <summary>
    /// A list is late when the day has come and things are still outstanding.
    /// </summary>
    /// <remarks>
    /// The one number this feature exists to put in front of somebody. A person arriving on
    /// Monday with no sign-in is a Monday nobody gets back.
    /// </remarks>
    [Fact]
    public async Task A_checklist_is_late_once_the_day_arrives_with_things_outstanding()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = new OnboardingService(new PeopleRepository(context), fixture.Clock);

        var employee = await Joined(fixture, context);
        var onboarding = await service.BeginAsync(employee, fixture.Clock.Today.AddDays(2));

        Assert.False(onboarding.IsLate(fixture.Clock.Today));

        fixture.Clock.Advance(TimeSpan.FromDays(3));

        Assert.True(onboarding.IsLate(fixture.Clock.Today));
    }

    /// <summary>
    /// What the firm handed over is what it asks back, and it is one record.
    /// </summary>
    /// <remarks>
    /// The loop section 15 closed, and the reason it replaced two lists with one. This used to
    /// assert that the joiner's list was copied into the leaver's — two records of the same
    /// laptop that could disagree, and neither of them able to say where a particular machine
    /// was. Now there is one row, and both screens read it: issuing on the first day is what
    /// makes it appear on the last.
    ///
    /// The refusal is asserted on the service rather than on the aggregate, because that is
    /// where the rule moved to when equipment stopped living on the offboarding row — and
    /// because the message it can now give names the tag, which is what somebody has to go and
    /// ask about.
    /// </remarks>
    [Fact]
    public async Task A_departure_cannot_be_closed_while_the_register_says_they_have_something()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();

        var repository = new PeopleRepository(context);
        var register = new AssetRepository(context);
        var people = new PeopleService(repository, register, fixture.Clock);
        var assets = new AssetService(register, fixture.Clock);

        var employee = await Joined(fixture, context);

        var laptop = await assets.BuyAsync(
            "JD-014",
            AssetKind.Laptop,
            "MacBook Air 13in",
            fixture.Clock.Today.AddDays(-7),
            "C02XK1JQ");

        await assets.IssueAsync(laptop.Id, employee, fixture.Clock.Today);

        await people.RecordLeavingAsync(
            employee, fixture.Clock.Today.AddDays(200), "Moving abroad");

        var leaving = await context.Offboardings.SingleAsync(one => one.EmployeeId == employee);

        leaving.AccessRemoved(Guid.CreateVersion7(), fixture.Clock.Now);
        await context.SaveChangesAsync();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => people.CompleteOffboardingAsync(employee));

        Assert.Contains("JD-014", refused.Message);

        await assets.TakeBackAsync(laptop.Id, fixture.Clock.Today, "Screen scratched");

        await people.CompleteOffboardingAsync(employee);

        var closed = await context.Offboardings.SingleAsync(one => one.EmployeeId == employee);

        Assert.True(closed.IsComplete);
    }

    private static async Task<Guid> Joined(DatabaseFixture fixture, TestDbContext context)
    {
        var employee = Employee.Hire(
            "Brian Otieno", fixture.Clock.Today.AddDays(14), null, "Delivery engineer");

        context.Employees.Add(employee);
        await context.SaveChangesAsync();

        return employee.Id;
    }
}
