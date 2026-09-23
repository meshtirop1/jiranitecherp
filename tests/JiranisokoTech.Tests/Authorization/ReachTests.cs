using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.Authorization;
using JiranisokoTech.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Tests.Authorization;

/// <summary>
/// How far somebody can see, when the answer is neither everything nor nothing.
/// </summary>
/// <remarks>
/// Section 5 had permissions at two grains — firm-wide, and your own record — and nothing
/// between them. A permission meaning "the projects you are on" could be declared and
/// granted, and the only two things a query could do with it were show everything or show
/// nothing. The search box chose everything, so every engineer in the firm could find
/// every project by name while holding a permission that said otherwise.
///
/// The most important test in this file is the one asserting that an empty limit is not
/// the same as no limit, because that confusion is exactly how a narrowing becomes a
/// widening.
/// </remarks>
public class ReachTests
{
    /// <summary>
    /// Nothing is not everything.
    /// </summary>
    /// <remarks>
    /// The whole reason Reach.Nothing is its own value rather than an empty set. A caller
    /// writing `if (ids.Count > 0) query = query.Where(...)` grants the firm to somebody
    /// who should see none of it, and the code reads as though it were being careful.
    /// </remarks>
    [Fact]
    public void An_empty_limit_is_not_the_absence_of_a_limit()
    {
        Assert.True(Reach.Everything.IsEverything);
        Assert.False(Reach.Nothing.IsEverything);

        Assert.False(Reach.Everything.IsNothing);
        Assert.True(Reach.Nothing.IsNothing);

        Assert.True(Reach.Everything.Includes(Guid.CreateVersion7()));
        Assert.False(Reach.Nothing.Includes(Guid.CreateVersion7()));
    }

    [Fact]
    public void A_limited_reach_includes_only_what_it_names()
    {
        var mine = Guid.CreateVersion7();
        var theirs = Guid.CreateVersion7();

        var reach = Reach.LimitedTo([mine]);

        Assert.True(reach.Includes(mine));
        Assert.False(reach.Includes(theirs));
        Assert.False(reach.IsEverything);
        Assert.False(reach.IsNothing);
    }

    /// <summary>
    /// Firm-wide permission reaches everything, and no query is narrowed.
    /// </summary>
    [Fact]
    public async Task Holding_the_firm_wide_permission_reaches_everything()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();

        var reach = await new Reaches(context).ProjectsAsync(
            new HashSet<string> { Permissions.ProjectsViewAll }, employeeId: null);

        Assert.True(reach.IsEverything);
    }

    /// <summary>
    /// The narrow permission reaches the projects somebody is on, and no others.
    /// </summary>
    /// <remarks>
    /// "On" means leading it or being assigned work under it — a definition about work
    /// rather than a membership table, because a table would be a second thing to keep
    /// current and would be wrong within a month.
    /// </remarks>
    [Fact]
    public async Task The_narrow_permission_reaches_the_projects_somebody_is_on()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();

        Guid person;
        Guid led;
        Guid worked;
        Guid neither;

        await using (var context = fixture.NewContext())
        {
            var employee = Employee.Hire("Meshack Tirop", new DateOnly(2026, 1, 5));
            context.Employees.Add(employee);

            var leading = Project.Begin("Leading this one", "lead");
            var working = Project.Begin("Working on this one", "work");
            var other = Project.Begin("Nothing to do with me", "other");

            context.Projects.AddRange(leading, working, other);
            await context.SaveChangesAsync();

            leading.LeadBy(employee.Id);

            var item = WorkItem.Raise(1, "Do the thing", employee.Id, working.Id);
            item.AssignTo(employee.Id);
            context.WorkItems.Add(item);

            await context.SaveChangesAsync();

            person = employee.Id;
            led = leading.Id;
            worked = working.Id;
            neither = other.Id;
        }

        await using var after = fixture.NewContext();

        var reach = await new Reaches(after).ProjectsAsync(
            new HashSet<string> { Permissions.ProjectsViewMember }, person);

        Assert.False(reach.IsEverything);
        Assert.True(reach.Includes(led));
        Assert.True(reach.Includes(worked));
        Assert.False(reach.Includes(neither));
    }

    /// <summary>
    /// An account with no staff record reaches nothing, not everything.
    /// </summary>
    /// <remarks>
    /// The default that matters. An unlinked account cannot be assigned work, so there is
    /// no honest set to give it — and in a method whose whole job is narrowing, everything
    /// is the wrong answer to "I do not know".
    /// </remarks>
    [Fact]
    public async Task An_account_with_no_staff_record_reaches_nothing()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();

        var reach = await new Reaches(context).ProjectsAsync(
            new HashSet<string> { Permissions.ProjectsViewMember }, employeeId: null);

        Assert.True(reach.IsNothing);
    }

    /// <summary>Holding neither permission reaches nothing.</summary>
    [Fact]
    public async Task Holding_no_project_permission_reaches_nothing()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();

        var reach = await new Reaches(context).ProjectsAsync(
            new HashSet<string>(), Guid.CreateVersion7());

        Assert.True(reach.IsNothing);
    }

    /// <summary>
    /// A department head reaches their own department and the ones they head.
    /// </summary>
    /// <remarks>
    /// Both halves. Somebody who heads nothing still works somewhere, and a roster showing
    /// them nobody — including themselves — would be a screen with no use at all.
    /// </remarks>
    [Fact]
    public async Task A_head_reaches_their_own_department_and_the_ones_they_head()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();

        Guid person;
        Guid headed;
        Guid theirOwn;
        Guid elsewhere;

        await using (var context = fixture.NewContext())
        {
            var engineering = Department.Open("Engineering", "engineering");
            var delivery = Department.Open("Delivery", "delivery");
            var finance = Department.Open("Finance", "finance");

            context.Departments.AddRange(engineering, delivery, finance);
            await context.SaveChangesAsync();

            var employee = Employee.Hire("Meshack Tirop", new DateOnly(2026, 1, 5));
            employee.MoveTo(delivery.Id);
            context.Employees.Add(employee);
            await context.SaveChangesAsync();

            engineering.AppointHead(employee.Id);
            await context.SaveChangesAsync();

            person = employee.Id;
            headed = engineering.Id;
            theirOwn = delivery.Id;
            elsewhere = finance.Id;
        }

        await using var after = fixture.NewContext();

        var reach = await new Reaches(after).DepartmentsAsync(
            new HashSet<string> { Permissions.EmployeesView }, person);

        Assert.True(reach.Includes(headed));
        Assert.True(reach.Includes(theirOwn));
        Assert.False(reach.Includes(elsewhere));
    }

    /// <summary>
    /// Only some roles see the whole staff list.
    /// </summary>
    /// <remarks>
    /// A new permission rather than a narrowing of employees.view, because that one is
    /// granted widely and redefining it in place would have quietly taken the roster away
    /// from every delivery manager in the firm.
    /// </remarks>
    [Fact]
    public void The_firm_wide_roster_is_held_by_fewer_roles_than_the_roster_itself()
    {
        var canOpen = Roles.All
            .Where(role => Roles.PermissionsFor(role).Contains(Permissions.EmployeesView))
            .ToList();

        var seesEverybody = Roles.All
            .Where(role => Roles.PermissionsFor(role).Contains(Permissions.EmployeesViewAll))
            .ToList();

        Assert.NotEmpty(seesEverybody);
        Assert.True(seesEverybody.Count < canOpen.Count);

        // A head answers for their own organisation and not for the firm's.
        Assert.DoesNotContain(Roles.DepartmentHead, seesEverybody);

        // HR keeps the staff records, so HR sees all of them.
        Assert.Contains(Roles.HumanResources, seesEverybody);
    }
}
