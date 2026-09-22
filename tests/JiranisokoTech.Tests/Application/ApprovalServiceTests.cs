using JiranisokoTech.Application.Approvals;
using JiranisokoTech.Application.People;
using JiranisokoTech.Domain.Approvals;
using JiranisokoTech.Infrastructure.Approvals;
using JiranisokoTech.Infrastructure.People;
using JiranisokoTech.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Tests.Application;

/// <summary>
/// Chains against a real database: who may be asked, and whether the steps
/// survive being written down.
/// </summary>
public class ApprovalServiceTests
{
    private static readonly DateOnly Monday = new(2026, 10, 5);

    private sealed class Module(DatabaseFixture db) : IAsyncDisposable
    {
        private readonly TestDbContext _context = db.NewContext();

        public PeopleService People => new(new PeopleRepository(_context));

        public ApprovalService Approvals =>
            new(new ApprovalRepository(_context), new PeopleRepository(_context), db.Clock);

        public ApprovalQueries Queries => new(_context);

        public ValueTask DisposeAsync() => _context.DisposeAsync();
    }

    private static async Task<Guid> WorkingAsync(Module module, string name)
    {
        var person = await module.People.HireAsync(name, Monday);
        await module.People.StartAsync(person.Id);

        return person.Id;
    }

    /// <summary>
    /// The round trip. Every rule on a chain is about the steps as a set, so a
    /// request that came back without them would answer every question wrongly
    /// and quietly.
    /// </summary>
    [Fact]
    public async Task A_chain_comes_back_with_its_steps_in_order()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var asker = await WorkingAsync(module, "Tirop Meshack");
        var lead = await WorkingAsync(module, "Charity Jepchirchir");
        var head = await WorkingAsync(module, "Vincent Bungei");

        var opened = await module.Approvals.RequestAsync(
            "JobRequisition", Guid.CreateVersion7(), "requisition.open", asker, [lead, head]);

        await using var read = db.NewContext();
        var stored = await read.Approvals
            .Include(request => request.Steps)
            .SingleAsync(request => request.Id == opened.Id);

        Assert.Equal(2, stored.Steps.Count);
        Assert.Equal(lead, stored.Steps[0].DeciderId);
        Assert.Equal(head, stored.Steps[1].DeciderId);
        Assert.Equal(lead, stored.WaitingOn);
        Assert.Equal(ApprovalStatus.Pending, stored.Status);
    }

    /// <summary>
    /// Somebody who has left will never answer, and a chain waiting on them sits
    /// at pending until a person notices — usually when somebody asks why a hire
    /// never happened.
    /// </summary>
    [Fact]
    public async Task Somebody_who_has_left_cannot_be_put_on_a_chain()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var asker = await WorkingAsync(module, "Tirop Meshack");
        var leaver = await WorkingAsync(module, "Duncan");

        await module.People.RecordLeavingAsync(leaver, Monday, "resigned");

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Approvals.RequestAsync(
                "JobRequisition", Guid.CreateVersion7(), "requisition.open", asker, [leaver]));

        Assert.Contains("cannot be asked to decide", refused.Message);
    }

    /// <summary>
    /// Two live chains over one decision produce two answers, and the second
    /// quietly overwrites the first.
    /// </summary>
    [Fact]
    public async Task A_second_chain_over_the_same_decision_is_refused()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var asker = await WorkingAsync(module, "Tirop Meshack");
        var head = await WorkingAsync(module, "Vincent Bungei");
        var subject = Guid.CreateVersion7();

        await module.Approvals.RequestAsync(
            "JobRequisition", subject, "requisition.open", asker, [head]);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Approvals.RequestAsync(
                "JobRequisition", subject, "requisition.open", asker, [head]));

        Assert.Contains("already an approval open", refused.Message);
    }

    /// <summary>
    /// Once settled, the same decision can be asked again. A refusal is not a
    /// permanent bar on ever asking.
    /// </summary>
    [Fact]
    public async Task A_settled_decision_can_be_asked_again()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var asker = await WorkingAsync(module, "Tirop Meshack");
        var head = await WorkingAsync(module, "Vincent Bungei");
        var subject = Guid.CreateVersion7();

        var first = await module.Approvals.RequestAsync(
            "JobRequisition", subject, "requisition.open", asker, [head]);

        await module.Approvals.RefuseAsync(first.Id, head, "no budget this quarter");

        var second = await module.Approvals.RequestAsync(
            "JobRequisition", subject, "requisition.open", asker, [head]);

        Assert.NotEqual(first.Id, second.Id);
    }

    /// <summary>
    /// The default chain is the reporting line: the people who should answer for
    /// a request are the ones the asker already answers to.
    /// </summary>
    [Fact]
    public async Task A_chain_can_be_built_from_the_reporting_line()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var director = await WorkingAsync(module, "Vincent Bungei");
        var head = await WorkingAsync(module, "Charity Jepchirchir");
        var engineer = await WorkingAsync(module, "Tirop Meshack");

        await module.People.SetReportingLineAsync(head, director);
        await module.People.SetReportingLineAsync(engineer, head);

        var request = await module.Approvals.RequestUpTheLineAsync(
            "Expense", Guid.CreateVersion7(), "expense.claim", engineer, levels: 2);

        Assert.Equal(2, request.Steps.Count);
        Assert.Equal(head, request.Steps[0].DeciderId);
        Assert.Equal(director, request.Steps[1].DeciderId);
    }

    /// <summary>
    /// A chain that silently reaches higher than intended is worse than one that
    /// refuses to open and says why.
    /// </summary>
    [Fact]
    public async Task Somebody_at_the_top_cannot_send_it_up_the_line()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var director = await WorkingAsync(module, "Vincent Bungei");

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Approvals.RequestUpTheLineAsync(
                "Expense", Guid.CreateVersion7(), "expense.claim", director));

        Assert.Contains("answers to nobody", refused.Message);
        Assert.Contains("Vincent Bungei", refused.Message);
    }

    /// <summary>
    /// The recovery path, end to end: the only decider has gone, the step cannot
    /// be passed over, and handing it on is what finishes the chain.
    /// </summary>
    [Fact]
    public async Task A_chain_stuck_on_somebody_who_left_is_finished_by_handing_it_on()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var asker = await WorkingAsync(module, "Tirop Meshack");
        var head = await WorkingAsync(module, "Charity Jepchirchir");
        var successor = await WorkingAsync(module, "Vincent Bungei");

        var request = await module.Approvals.RequestAsync(
            "JobRequisition", Guid.CreateVersion7(), "requisition.open", asker, [head]);

        await module.People.RecordLeavingAsync(head, Monday, "resigned");

        // The only step left, so it cannot be passed over.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Approvals.SkipAsync(request.Id, 1, "the head has left"));

        await module.Approvals.ReassignAsync(request.Id, 1, successor);
        await module.Approvals.ApproveAsync(request.Id, successor, "agreed");

        await using var read = db.NewContext();
        var settled = await read.Approvals
            .Include(one => one.Steps)
            .SingleAsync(one => one.Id == request.Id);

        Assert.Equal(ApprovalStatus.Approved, settled.Status);
        Assert.Equal(successor, settled.Steps[0].DecidedById);
    }

    /// <summary>
    /// Nobody is shown a decision before it is their turn, or they learn to
    /// ignore the list.
    /// </summary>
    [Fact]
    public async Task What_is_waiting_on_me_shows_only_my_turn()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var asker = await WorkingAsync(module, "Tirop Meshack");
        var lead = await WorkingAsync(module, "Charity Jepchirchir");
        var head = await WorkingAsync(module, "Vincent Bungei");

        var request = await module.Approvals.RequestAsync(
            "JobRequisition", Guid.CreateVersion7(), "requisition.open", asker, [lead, head]);

        Assert.Single(await module.Queries.WaitingOnAsync(lead));
        Assert.Empty(await module.Queries.WaitingOnAsync(head));

        await module.Approvals.ApproveAsync(request.Id, lead);

        Assert.Empty(await module.Queries.WaitingOnAsync(lead));
        Assert.Single(await module.Queries.WaitingOnAsync(head));
    }

    [Fact]
    public async Task A_settled_chain_waits_on_nobody()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var asker = await WorkingAsync(module, "Tirop Meshack");
        var head = await WorkingAsync(module, "Vincent Bungei");

        var request = await module.Approvals.RequestAsync(
            "JobRequisition", Guid.CreateVersion7(), "requisition.open", asker, [head]);

        await module.Approvals.ApproveAsync(request.Id, head);

        Assert.Empty(await module.Queries.WaitingOnAsync(head));
        Assert.Empty(await module.Queries.PendingAsync());
    }
}
