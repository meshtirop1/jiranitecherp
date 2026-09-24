using JiranisokoTech.Application.People;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Infrastructure.People;
using JiranisokoTech.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Tests.People;

/// <summary>
/// Teams, and the ways they are not departments.
/// </summary>
/// <remarks>
/// Section 6's first unticked row asked for teams "distinct from departments", and the first
/// test here is the one that makes that more than a sentence on a screen: joining a team changes
/// nothing about a staff record. If it ever did, teams would have become sub-departments by
/// accident, and the cross-department team — three engineers, a designer and somebody from the
/// office — would have to take people out of the department that answers for them.
/// </remarks>
public class TeamTests
{
    /// <summary>
    /// Joining a team leaves the staff record alone.
    /// </summary>
    /// <remarks>
    /// The whole reason this is a second aggregate. A team crossing departments is the ordinary
    /// case, and if membership moved somebody's department it would break leave approval — the
    /// approver is decided from the reporting line, and the reporting line sits on the record
    /// this test says nothing touches.
    /// </remarks>
    [Fact]
    public async Task Joining_a_team_changes_nothing_about_where_somebody_works()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var delivery = Department.Open("Delivery", "delivery");
        var office = Department.Open("Office", "office");

        context.Departments.AddRange(delivery, office);
        await context.SaveChangesAsync();

        var engineer = await Somebody(fixture, context, "Brian Kiptoo", delivery.Id);
        var accountant = await Somebody(fixture, context, "Faith Njeri", office.Id);

        var team = await service.FormAsync("Platform", "platform", "Keeps the hosting");

        await service.JoinAsync(team.Id, engineer);
        await service.JoinAsync(team.Id, accountant);

        var after = await context.Employees
            .AsNoTracking()
            .Where(one => one.Id == engineer || one.Id == accountant)
            .ToListAsync();

        Assert.Equal(delivery.Id, after.Single(one => one.Id == engineer).DepartmentId);
        Assert.Equal(office.Id, after.Single(one => one.Id == accountant).DepartmentId);

        // And the team spans both, which is the thing a sub-department could not express.
        var loaded = await service.OneAsync(team.Id);

        Assert.Equal(2, loaded!.Size);
    }

    [Fact]
    public async Task Two_teams_cannot_share_an_address()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        await service.FormAsync("Platform", "platform");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.FormAsync("Platform engineering", "platform"));

        Assert.Contains("different short name", refusal.Message);
    }

    /// <summary>
    /// Somebody cannot be put on a team twice.
    /// </summary>
    /// <remarks>
    /// Not a tidiness rule. Two current spells would make the size wrong and make "is this
    /// person on the team" a question with two answers, and it cannot be enforced by a unique
    /// index — see the note in the configuration about nulls being distinct.
    /// </remarks>
    [Fact]
    public async Task Somebody_cannot_be_on_a_team_twice()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var person = await Somebody(fixture, context, "Brian Kiptoo", null);
        var team = await service.FormAsync("Platform", "platform");

        await service.JoinAsync(team.Id, person);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.JoinAsync(team.Id, person));

        Assert.Contains("already on this team", refusal.Message);
        Assert.Equal(1, (await service.OneAsync(team.Id))!.Size);
    }

    /// <summary>
    /// Somebody lent out and brought back has two spells.
    /// </summary>
    /// <remarks>
    /// The case the refusal above is careful not to catch. Flattening two spells into one
    /// membership with the earlier date would claim they never left, and "who was on this team
    /// in April" is the question the history exists to answer.
    /// </remarks>
    [Fact]
    public async Task Rejoining_keeps_both_spells()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var person = await Somebody(fixture, context, "Brian Kiptoo", null);
        var team = await service.FormAsync("Platform", "platform");

        await service.JoinAsync(team.Id, person, fixture.Clock.Today.AddMonths(-6));
        await service.LeaveAsync(team.Id, person, fixture.Clock.Today.AddMonths(-3));
        await service.JoinAsync(team.Id, person, fixture.Clock.Today);

        var loaded = await service.OneAsync(team.Id);

        Assert.Equal(2, loaded!.Members.Count);
        Assert.Equal(1, loaded.Size);
        Assert.True(loaded.Has(person));
    }

    [Fact]
    public async Task Nobody_leaves_a_team_before_they_joined_it()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var person = await Somebody(fixture, context, "Brian Kiptoo", null);
        var team = await service.FormAsync("Platform", "platform");

        await service.JoinAsync(team.Id, person, fixture.Clock.Today);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.LeaveAsync(team.Id, person, fixture.Clock.Today.AddDays(-1)));
    }

    /// <summary>
    /// A team is led by somebody on it, and the post goes when they leave it.
    /// </summary>
    /// <remarks>
    /// A lead who is not in the list underneath their own name reads as a bug in the list, and
    /// the lead is who somebody asks about the team's work — which somebody who is not on it
    /// cannot answer.
    /// </remarks>
    [Fact]
    public async Task A_lead_has_to_be_on_the_team_and_stops_being_lead_when_they_leave()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var person = await Somebody(fixture, context, "Brian Kiptoo", null);
        var outsider = await Somebody(fixture, context, "Faith Njeri", null);
        var team = await service.FormAsync("Platform", "platform");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.LeadAsync(team.Id, outsider));

        Assert.Contains("Put them on the team first", refusal.Message);

        await service.JoinAsync(team.Id, person);
        await service.LeadAsync(team.Id, person);

        Assert.Equal(person, (await service.OneAsync(team.Id))!.LeadEmployeeId);

        await service.LeaveAsync(team.Id, person);

        Assert.Null((await service.OneAsync(team.Id))!.LeadEmployeeId);
    }

    /// <summary>
    /// Disbanding ends everybody's spell and keeps the record.
    /// </summary>
    /// <remarks>
    /// Ending them rather than leaving them open, because a disbanded team with current members
    /// would go on appearing on every one of those people's staff pages — which is a team that
    /// does not exist telling somebody what they are working on.
    /// </remarks>
    [Fact]
    public async Task Disbanding_ends_the_spells_and_deletes_nothing()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var person = await Somebody(fixture, context, "Brian Kiptoo", null);
        var team = await service.FormAsync("Platform", "platform");

        await service.JoinAsync(team.Id, person);
        await service.LeadAsync(team.Id, person);
        await service.DisbandAsync(team.Id);

        var loaded = await service.OneAsync(team.Id);

        Assert.False(loaded!.IsActive);
        Assert.Equal(0, loaded.Size);
        Assert.Single(loaded.Members);
        Assert.Null(loaded.LeadEmployeeId);

        // And it is off the person's page, which asks what they are working on now.
        Assert.Empty(await service.ForEmployeeAsync(person));

        // But still on file, and not in the ordinary list.
        Assert.Empty(await service.AllAsync());
        Assert.Single(await service.AllAsync(includeDisbanded: true));
    }

    /// <summary>
    /// A leaver cannot be put on a team.
    /// </summary>
    /// <remarks>
    /// A team is a list of people somebody is expecting work from, and a leaver on one is a
    /// commitment nobody is going to keep — discovered at the next stand-up rather than by
    /// anybody reading the list.
    /// </remarks>
    [Fact]
    public async Task A_leaver_cannot_be_put_on_a_team()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var person = await Somebody(fixture, context, "Brian Kiptoo", null);
        var record = await context.Employees.FirstAsync(one => one.Id == person);

        record.Start();
        record.Leave(fixture.Clock.Today, "took a job elsewhere");
        await context.SaveChangesAsync();

        var team = await service.FormAsync("Platform", "platform");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.JoinAsync(team.Id, person));

        Assert.Contains("has left", refusal.Message);
    }

    private static TeamService Service(DatabaseFixture fixture, TestDbContext context) =>
        new(new TeamRepository(context), new PeopleRepository(context), fixture.Clock);

    private static async Task<Guid> Somebody(
        DatabaseFixture fixture, TestDbContext context, string name, Guid? departmentId)
    {
        var employee = Employee.Hire(name, fixture.Clock.Today, departmentId, "Engineer");

        context.Employees.Add(employee);
        await context.SaveChangesAsync();

        return employee.Id;
    }
}
