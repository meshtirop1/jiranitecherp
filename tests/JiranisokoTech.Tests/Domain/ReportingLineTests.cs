using JiranisokoTech.Domain.People;

namespace JiranisokoTech.Tests.Domain;

/// <summary>
/// The rule that keeps the organisation a tree.
/// </summary>
/// <remarks>
/// A loop in the reporting lines is not an unusual organisation, it is corrupt
/// data: nothing can draw it, and the page that walks the chain to find an
/// approver hangs rather than failing — which is the worst way for it to go
/// wrong, because it looks like slowness.
/// </remarks>
public class ReportingLineTests
{
    private readonly Guid _managingDirector = Guid.CreateVersion7();
    private readonly Guid _head = Guid.CreateVersion7();
    private readonly Guid _lead = Guid.CreateVersion7();
    private readonly Guid _engineer = Guid.CreateVersion7();

    /// <summary>Engineer → lead → head → managing director → nobody.</summary>
    private Dictionary<Guid, Guid?> Firm() => new()
    {
        [_engineer] = _lead,
        [_lead] = _head,
        [_head] = _managingDirector,
        [_managingDirector] = null,
    };

    private Func<Guid, Guid?> Lookup(Dictionary<Guid, Guid?> firm) =>
        id => firm.GetValueOrDefault(id);

    [Fact]
    public void An_ordinary_line_is_allowed()
    {
        var firm = Firm();

        Assert.False(ReportingLine.WouldCycle(_engineer, _head, Lookup(firm)));
    }

    [Fact]
    public void Reporting_to_yourself_is_a_loop()
    {
        Assert.True(ReportingLine.WouldCycle(_engineer, _engineer, Lookup(Firm())));
    }

    /// <summary>
    /// The case a check on the two people alone would miss: the head answering
    /// to the engineer who already answers, through two more people, to them.
    /// </summary>
    [Fact]
    public void A_line_that_closes_further_up_the_chain_is_a_loop()
    {
        Assert.True(ReportingLine.WouldCycle(_head, _engineer, Lookup(Firm())));
    }

    [Fact]
    public void Answering_to_nobody_cannot_close_anything()
    {
        Assert.False(ReportingLine.WouldCycle(_engineer, null, Lookup(Firm())));
    }

    [Fact]
    public void An_unknown_manager_cannot_be_part_of_a_loop()
    {
        Assert.False(ReportingLine.WouldCycle(_engineer, Guid.CreateVersion7(), Lookup(Firm())));
    }

    /// <summary>
    /// If the data is already looped, this must refuse rather than walk it. The
    /// walk is the thing that would never end.
    /// </summary>
    [Fact]
    public async Task A_loop_that_is_already_there_is_refused_rather_than_followed()
    {
        var broken = Firm();
        broken[_managingDirector] = _lead;

        var looped = await Answered(
            () => ReportingLine.WouldCycle(Guid.CreateVersion7(), _engineer, Lookup(broken)));

        Assert.True(looped);
    }

    [Fact]
    public void The_chain_above_somebody_runs_nearest_first_to_the_top()
    {
        var chain = ReportingLine.ChainAbove(_engineer, Lookup(Firm()));

        Assert.Equal([_lead, _head, _managingDirector], chain);
    }

    [Fact]
    public void The_person_at_the_top_has_nobody_above_them()
    {
        Assert.Empty(ReportingLine.ChainAbove(_managingDirector, Lookup(Firm())));
    }

    /// <summary>
    /// Data that is already broken must produce a short answer rather than a
    /// hung page.
    /// </summary>
    [Fact]
    public async Task Walking_a_looped_chain_stops_instead_of_running_forever()
    {
        var broken = Firm();
        broken[_managingDirector] = _engineer;

        var chain = await Answered(() => ReportingLine.ChainAbove(_engineer, Lookup(broken)));

        Assert.Equal([_lead, _head, _managingDirector], chain);
    }

    /// <summary>
    /// Run it with a deadline, so that a walk which no longer terminates fails
    /// the suite instead of hanging it.
    /// </summary>
    /// <remarks>
    /// Both functions have a hard step limit, so they provably come back. This
    /// is here for the day somebody removes that limit as an obvious tidy-up: a
    /// test that hangs stops the build with no explanation, and one that fails
    /// names the reason.
    /// </remarks>
    private static Task<T> Answered<T>(Func<T> work) =>
        Task.Run(work).WaitAsync(TimeSpan.FromSeconds(5));
}
