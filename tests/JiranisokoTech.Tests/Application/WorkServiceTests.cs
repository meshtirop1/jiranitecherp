using JiranisokoTech.Application.People;
using JiranisokoTech.Application.Work;
using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.Assets;
using JiranisokoTech.Infrastructure.People;
using JiranisokoTech.Infrastructure.Work;
using JiranisokoTech.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Tests.Application;

/// <summary>
/// The work rules that reach outside one row: who may be given work, and what a
/// project has to look like before it can be called delivered.
/// </summary>
public class WorkServiceTests
{
    private static readonly DateOnly Monday = new(2026, 10, 5);

    private sealed class Module(DatabaseFixture db) : IAsyncDisposable
    {
        private readonly TestDbContext _context = db.NewContext();

        public PeopleService People => new(new PeopleRepository(_context), new AssetRepository(_context), db.Clock);

        public WorkService Work =>
            new(
                new WorkRepository(_context),
                new PeopleRepository(_context),
                new PlanningRepository(_context),
                db.Clock);

        public ValueTask DisposeAsync() => _context.DisposeAsync();
    }

    [Fact]
    public async Task A_project_begins_with_a_code_of_its_own()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var project = await module.Work.BeginProjectAsync("Delivery Note Printer");

        Assert.Equal("delivery-note-printer", project.Code);
        Assert.Equal(ProjectStatus.Planned, project.Status);
    }

    /// <summary>
    /// Codes end up in addresses, commit messages and conversation, so two
    /// projects cannot share one.
    /// </summary>
    [Fact]
    public async Task A_second_project_cannot_take_the_same_code()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        await module.Work.BeginProjectAsync("Delivery Note Printer");

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Work.BeginProjectAsync("delivery note printer"));

        Assert.Contains("delivery-note-printer", refused.Message);
    }

    /// <summary>
    /// The rule this service exists for. A project marked delivered with live
    /// work under it is either wrong, or a decision somebody should make
    /// deliberately rather than one the system makes on their behalf.
    /// </summary>
    [Fact]
    public async Task A_project_with_open_work_cannot_be_delivered()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var project = await module.Work.BeginProjectAsync("Delivery note printer");
        await module.Work.ActivateProjectAsync(project.Id);
        await module.Work.RaiseAsync("Print a note", Guid.CreateVersion7(), project.Id);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Work.DeliverProjectAsync(project.Id));

        Assert.Contains("1 item(s) open", refused.Message);

        await using var read = db.NewContext();
        Assert.Equal(
            ProjectStatus.Active,
            (await read.Projects.SingleAsync(p => p.Id == project.Id)).Status);
    }

    [Fact]
    public async Task A_project_whose_work_is_all_finished_can_be_delivered()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var project = await module.Work.BeginProjectAsync("Delivery note printer");
        await module.Work.ActivateProjectAsync(project.Id);

        var item = await module.Work.RaiseAsync("Print a note", Guid.CreateVersion7(), project.Id);

        await module.Work.MoveAsync(item.Id, WorkItemStatus.InProgress);
        await module.Work.MoveAsync(item.Id, WorkItemStatus.InReview);
        await module.Work.MoveAsync(item.Id, WorkItemStatus.Done);

        await module.Work.DeliverProjectAsync(project.Id);

        await using var read = db.NewContext();
        Assert.Equal(
            ProjectStatus.Delivered,
            (await read.Projects.SingleAsync(p => p.Id == project.Id)).Status);
    }

    /// <summary>
    /// Cancelled work does not hold a project open. It was a decision, and the
    /// decision has been made.
    /// </summary>
    [Fact]
    public async Task Cancelled_work_does_not_hold_a_project_open()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var project = await module.Work.BeginProjectAsync("Delivery note printer");
        await module.Work.ActivateProjectAsync(project.Id);

        var item = await module.Work.RaiseAsync("Not needed", Guid.CreateVersion7(), project.Id);
        await module.Work.MoveAsync(item.Id, WorkItemStatus.Cancelled);

        await module.Work.DeliverProjectAsync(project.Id);

        await using var read = db.NewContext();
        Assert.Equal(
            ProjectStatus.Delivered,
            (await read.Projects.SingleAsync(p => p.Id == project.Id)).Status);
    }

    [Fact]
    public async Task No_new_work_is_added_to_a_delivered_project()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var project = await module.Work.BeginProjectAsync("Delivery note printer");
        await module.Work.ActivateProjectAsync(project.Id);
        await module.Work.DeliverProjectAsync(project.Id);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Work.RaiseAsync("One more thing", Guid.CreateVersion7(), project.Id));

        Assert.Contains("delivered", refused.Message);
    }

    /// <summary>
    /// A board that looks staffed and is not is worse than an empty one:
    /// nobody notices until the deadline.
    /// </summary>
    [Fact]
    public async Task Work_cannot_be_given_to_somebody_who_has_not_started()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var joiner = await module.People.HireAsync("Precious", Monday);
        var item = await module.Work.RaiseAsync("Print a note", Guid.CreateVersion7());

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Work.AssignAsync(item.Id, joiner.Id));

        Assert.Contains("has not started", refused.Message);
    }

    [Fact]
    public async Task Work_cannot_be_given_to_somebody_who_has_left()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var leaver = await module.People.HireAsync("Duncan", Monday);
        await module.People.StartAsync(leaver.Id);
        await module.People.RecordLeavingAsync(leaver.Id, Monday, "resigned");

        var item = await module.Work.RaiseAsync("Print a note", Guid.CreateVersion7());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Work.AssignAsync(item.Id, leaver.Id));
    }

    [Fact]
    public async Task Work_can_be_given_to_somebody_who_is_here()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var engineer = await module.People.HireAsync("Tirop Meshack", Monday);
        await module.People.StartAsync(engineer.Id);

        var item = await module.Work.RaiseAsync("Print a note", Guid.CreateVersion7());
        await module.Work.AssignAsync(item.Id, engineer.Id);

        await using var read = db.NewContext();
        Assert.Equal(
            engineer.Id,
            (await read.WorkItems.SingleAsync(i => i.Id == item.Id)).AssigneeId);
    }

    /// <summary>
    /// Work needing a new owner should look like it needs one. Loading it onto
    /// a manager who has not agreed to it is how it gets lost twice.
    /// </summary>
    [Fact]
    public async Task Open_work_is_released_when_its_owner_goes()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var engineer = await module.People.HireAsync("Duncan", Monday);
        await module.People.StartAsync(engineer.Id);

        var open = await module.Work.RaiseAsync("Still to do", Guid.CreateVersion7());
        var finished = await module.Work.RaiseAsync("Already done", Guid.CreateVersion7());

        await module.Work.AssignAsync(open.Id, engineer.Id);
        await module.Work.AssignAsync(finished.Id, engineer.Id);

        await module.Work.MoveAsync(finished.Id, WorkItemStatus.InProgress);
        await module.Work.MoveAsync(finished.Id, WorkItemStatus.InReview);
        await module.Work.MoveAsync(finished.Id, WorkItemStatus.Done);

        var released = await module.Work.ReleaseWorkOfAsync(engineer.Id);

        Assert.Equal(1, released);

        await using var read = db.NewContext();

        Assert.Null((await read.WorkItems.SingleAsync(i => i.Id == open.Id)).AssigneeId);

        // Finished work keeps their name on it. That is the record of who did it.
        Assert.Equal(
            engineer.Id,
            (await read.WorkItems.SingleAsync(i => i.Id == finished.Id)).AssigneeId);
    }

    [Fact]
    public async Task Raising_work_against_a_project_that_does_not_exist_is_refused()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Work.RaiseAsync("Nowhere", Guid.CreateVersion7(), Guid.CreateVersion7()));
    }
}
