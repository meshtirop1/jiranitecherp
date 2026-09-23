using JiranisokoTech.Application.People;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Infrastructure.People;
using JiranisokoTech.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Tests.Application;

/// <summary>
/// The rules that need more than one row.
/// </summary>
/// <remarks>
/// Driven against a real database rather than a stand-in repository. Every rule
/// here is about several rows agreeing with each other, and a hand-written
/// double agrees with whatever the test expects — which is how a suite comes to
/// prove that the test author understood the rule rather than that the code
/// implements it.
/// </remarks>
public class PeopleServiceTests
{
    private static readonly DateOnly Monday = new(2026, 10, 5);

    private sealed class Module(DatabaseFixture db) : IAsyncDisposable
    {
        private readonly TestDbContext _context = db.NewContext();

        public PeopleService Service => new(new PeopleRepository(_context), db.Clock);

        public TestDbContext Context => _context;

        public ValueTask DisposeAsync() => _context.DisposeAsync();
    }

    [Fact]
    public async Task A_department_is_opened_with_an_address_of_its_own()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var department = await module.Service.OpenDepartmentAsync("Field Operations");

        Assert.Equal("field-operations", department.Slug);
        Assert.Equal(1, await module.Context.Departments.CountAsync());
    }

    /// <summary>
    /// The handle is what an address names, so the second one has to be
    /// refused with a message somebody can act on rather than a constraint
    /// violation from the database.
    /// </summary>
    [Fact]
    public async Task A_second_department_cannot_take_the_same_address()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        await module.Service.OpenDepartmentAsync("Field Operations");

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Service.OpenDepartmentAsync("field operations"));

        Assert.Contains("field-operations", refused.Message);
    }

    [Fact]
    public async Task Somebody_can_be_hired_straight_into_a_department_under_a_manager()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var engineering = await module.Service.OpenDepartmentAsync("Engineering");
        var head = await module.Service.HireAsync("Charity Jepchirchir", Monday, engineering.Id);

        var engineer = await module.Service.HireAsync(
            "Tirop Meshack", Monday, engineering.Id, "Engineer", head.Id);

        Assert.Equal(engineering.Id, engineer.DepartmentId);
        Assert.Equal(head.Id, engineer.ReportsToId);
    }

    [Fact]
    public async Task Somebody_cannot_be_hired_into_a_department_that_does_not_exist()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Service.HireAsync("Nobody", Monday, Guid.CreateVersion7()));
    }

    /// <summary>
    /// The rule this service exists for. A to B to C to A is not an unusual
    /// organisation; it is data that nothing can draw and that makes the
    /// question "who approves this?" run forever.
    /// </summary>
    [Fact]
    public async Task A_reporting_line_that_would_close_a_loop_is_refused()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var director = await module.Service.HireAsync("Vincent Bungei", Monday);
        var head = await module.Service.HireAsync("Charity Jepchirchir", Monday, reportsToId: director.Id);
        var engineer = await module.Service.HireAsync("Tirop Meshack", Monday, reportsToId: head.Id);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Service.SetReportingLineAsync(director.Id, engineer.Id));

        Assert.Contains("loop", refused.Message);

        // And nothing was written: a refusal that half-applies is worse than one
        // that does not apply at all.
        await using var read = db.NewContext();
        Assert.Null((await read.Employees.SingleAsync(e => e.Id == director.Id)).ReportsToId);
    }

    [Fact]
    public async Task An_ordinary_reporting_line_is_recorded()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var head = await module.Service.HireAsync("Charity Jepchirchir", Monday);
        var engineer = await module.Service.HireAsync("Tirop Meshack", Monday);

        await module.Service.SetReportingLineAsync(engineer.Id, head.Id);

        await using var read = db.NewContext();
        Assert.Equal(head.Id, (await read.Employees.SingleAsync(e => e.Id == engineer.Id)).ReportsToId);
    }

    [Fact]
    public async Task Nobody_is_made_to_report_to_somebody_who_has_left()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var leaver = await module.Service.HireAsync("Duncan", Monday);
        await module.Service.StartAsync(leaver.Id);
        await module.Service.RecordLeavingAsync(leaver.Id, Monday, "resigned");

        var engineer = await module.Service.HireAsync("Tirop Meshack", Monday);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Service.SetReportingLineAsync(engineer.Id, leaver.Id));

        Assert.Contains("has left", refused.Message);
    }

    /// <summary>
    /// The consequence that matters when somebody leaves.
    /// </summary>
    /// <remarks>
    /// Their reports would otherwise answer to a person who has gone, and the
    /// next approval routed up the chain would stop at somebody who cannot act.
    /// Moving them up to the leaver's own manager is a guess, but it is the
    /// guess an administrator would make.
    /// </remarks>
    [Fact]
    public async Task When_somebody_leaves_their_reports_move_up_a_level()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var director = await module.Service.HireAsync("Vincent Bungei", Monday);
        var head = await module.Service.HireAsync("Charity Jepchirchir", Monday, reportsToId: director.Id);
        var first = await module.Service.HireAsync("Tirop Meshack", Monday, reportsToId: head.Id);
        var second = await module.Service.HireAsync("Purity", Monday, reportsToId: head.Id);

        await module.Service.StartAsync(head.Id);
        var moved = await module.Service.RecordLeavingAsync(head.Id, Monday, "resigned");

        Assert.Equal(2, moved);

        await using var read = db.NewContext();

        Assert.Equal(director.Id, (await read.Employees.SingleAsync(e => e.Id == first.Id)).ReportsToId);
        Assert.Equal(director.Id, (await read.Employees.SingleAsync(e => e.Id == second.Id)).ReportsToId);
    }

    /// <summary>
    /// A post held by somebody who has gone keeps granting the role that comes
    /// with it. An empty post that shows as empty is the lesser problem.
    /// </summary>
    [Fact]
    public async Task When_a_head_leaves_their_department_is_left_without_one()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var engineering = await module.Service.OpenDepartmentAsync("Engineering");
        var head = await module.Service.HireAsync("Charity Jepchirchir", Monday, engineering.Id);

        await module.Service.AppointHeadAsync(engineering.Id, head.Id);
        await module.Service.StartAsync(head.Id);
        await module.Service.RecordLeavingAsync(head.Id, Monday, "resigned");

        await using var read = db.NewContext();
        var stored = await read.Departments.SingleAsync(d => d.Id == engineering.Id);

        Assert.Null(stored.HeadEmployeeId);
    }

    [Fact]
    public async Task Somebody_who_has_left_cannot_be_put_in_charge_of_anything()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var engineering = await module.Service.OpenDepartmentAsync("Engineering");
        var leaver = await module.Service.HireAsync("Duncan", Monday);

        await module.Service.StartAsync(leaver.Id);
        await module.Service.RecordLeavingAsync(leaver.Id, Monday, "resigned");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Service.AppointHeadAsync(engineering.Id, leaver.Id));
    }

    [Fact]
    public async Task Somebody_who_does_not_work_here_cannot_be_put_in_charge()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var engineering = await module.Service.OpenDepartmentAsync("Engineering");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Service.AppointHeadAsync(engineering.Id, Guid.CreateVersion7()));
    }

    [Fact]
    public async Task Moving_somebody_to_a_department_that_does_not_exist_is_refused()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var employee = await module.Service.HireAsync("Tirop Meshack", Monday);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Service.MoveAsync(employee.Id, Guid.CreateVersion7()));
    }
}
