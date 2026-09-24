using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.Performance;

namespace JiranisokoTech.Application.People;

/// <summary>What goals and reviews need read and written.</summary>
public interface IPerformanceRepository
{
    Task<Goal?> FindGoalAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>One person's goals, open ones first.</summary>
    Task<List<Goal>> GoalsForAsync(
        Guid employeeId, CancellationToken cancellationToken = default);

    /// <summary>Open goals past their date, for everybody in a set of people.</summary>
    Task<List<Goal>> OverdueAsync(
        IReadOnlyCollection<Guid> employeeIds,
        DateOnly today,
        CancellationToken cancellationToken = default);

    Task<ReviewCycle?> FindCycleAsync(Guid id, CancellationToken cancellationToken = default);

    Task<List<ReviewCycle>> CyclesAsync(CancellationToken cancellationToken = default);

    /// <summary>The cycles one person has a review in, newest period first.</summary>
    Task<List<ReviewCycle>> CyclesForAsync(
        Guid employeeId, CancellationToken cancellationToken = default);

    void Add(Goal goal);

    void Add(ReviewCycle cycle);

    Task SaveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Goals, and the review cycles they are talked about in.
/// </summary>
/// <remarks>
/// Section 6.
///
/// <b>A rating is never computed from goal outcomes.</b> The arithmetic is right there — four
/// goals, three achieved — and using it is what turns setting goals into negotiating how easy
/// this year's should be. A goal missed because the firm changed direction is normal, and a
/// number that cannot tell that from somebody not doing their job is a number that makes the
/// conversation worse. So the two live side by side here and nothing joins them up.
///
/// <b>The two halves of a review are written by two people and only one of them is visible
/// early.</b> Those rules are in the aggregate rather than here, because a page could forget
/// them and the harm — somebody reading a half-finished appraisal of themselves — is the worst
/// thing this feature could do.
/// </remarks>
public sealed class PerformanceService(
    IPerformanceRepository performance, IPeopleRepository people, IClock clock)
{
    /// <summary>
    /// Set a goal with somebody.
    /// </summary>
    /// <remarks>
    /// The person has to work here and not have left. A goal on a leaver's record is a commitment
    /// with nobody at either end of it.
    /// </remarks>
    public async Task<Goal> SetGoalAsync(
        Guid forEmployeeId,
        Guid setByEmployeeId,
        string title,
        string measure,
        DateOnly from,
        DateOnly to,
        Guid? cycleId = null,
        CancellationToken cancellationToken = default)
    {
        var person = await people.FindAsync(forEmployeeId, cancellationToken)
            ?? throw new InvalidOperationException("That person is not on the staff list.");

        if (person.Status == Domain.People.EmploymentStatus.Left)
        {
            throw new InvalidOperationException(
                $"{person.FullName} has left. A goal is an agreement with somebody who is here.");
        }

        if (cycleId is { } cycle
            && await performance.FindCycleAsync(cycle, cancellationToken) is null)
        {
            throw new InvalidOperationException("That review cycle does not exist.");
        }

        var goal = Goal.Set(
            forEmployeeId, setByEmployeeId, title, measure, from, to, clock.Now, cycleId);

        performance.Add(goal);
        await performance.SaveAsync(cancellationToken);

        return goal;
    }

    public async Task DescribeGoalAsync(
        Guid id,
        string title,
        string measure,
        string? detail,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        var goal = await RequiredGoal(id, cancellationToken);

        goal.Describe(title, measure, detail);
        goal.Runs(from, to);

        await performance.SaveAsync(cancellationToken);
    }

    public async Task NoteGoalAsync(
        Guid id, Guid byEmployeeId, string note, CancellationToken cancellationToken = default)
    {
        var goal = await RequiredGoal(id, cancellationToken);

        goal.Note(byEmployeeId, note, clock.Now);

        await performance.SaveAsync(cancellationToken);
    }

    public async Task CloseGoalAsync(
        Guid id,
        GoalOutcome outcome,
        string verdict,
        CancellationToken cancellationToken = default)
    {
        var goal = await RequiredGoal(id, cancellationToken);

        goal.Close(outcome, verdict, clock.Now);

        await performance.SaveAsync(cancellationToken);
    }

    public async Task ReopenGoalAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var goal = await RequiredGoal(id, cancellationToken);

        goal.Reopen();

        await performance.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Open a cycle over everybody who works here.
    /// </summary>
    /// <remarks>
    /// Everybody who has started and not left, with their manager taken from the reporting line
    /// as it stands today and then kept. Including people rather than waiting for them to appear
    /// is the whole point: a list that fills up as people participate cannot show who has not.
    ///
    /// Somebody with nobody above them — the owner — gets a review with no manager rather than
    /// no review. It is the one place a self-assessment stands on its own, and leaving them out
    /// would make the cycle quietly incomplete.
    /// </remarks>
    public async Task<ReviewCycle> OpenCycleAsync(
        string name, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var cycle = ReviewCycle.Open(name, from, to);
        var lines = await people.ReportingLinesAsync(cancellationToken);

        foreach (var person in await people.EverybodyHereAsync(cancellationToken))
        {
            cycle.Include(person.Id, lines.GetValueOrDefault(person.Id));
        }

        performance.Add(cycle);
        await performance.SaveAsync(cancellationToken);

        return cycle;
    }

    public async Task WriteSelfAsync(
        Guid cycleId, Guid employeeId, string note, CancellationToken cancellationToken = default)
    {
        var cycle = await RequiredCycle(cycleId, cancellationToken);

        cycle.WriteSelf(employeeId, note, clock.Now);

        await performance.SaveAsync(cancellationToken);
    }

    public async Task WriteManagerAsync(
        Guid cycleId,
        Guid employeeId,
        Guid byEmployeeId,
        string note,
        ReviewRating? rating,
        CancellationToken cancellationToken = default)
    {
        var cycle = await RequiredCycle(cycleId, cancellationToken);

        cycle.WriteManager(employeeId, byEmployeeId, note, rating, clock.Now);

        await performance.SaveAsync(cancellationToken);
    }

    public async Task ShareAsync(
        Guid cycleId, Guid employeeId, CancellationToken cancellationToken = default)
    {
        var cycle = await RequiredCycle(cycleId, cancellationToken);

        cycle.Share(employeeId, clock.Now);

        await performance.SaveAsync(cancellationToken);
    }

    public async Task RenameCycleAsync(
        Guid id, string name, CancellationToken cancellationToken = default)
    {
        var cycle = await RequiredCycle(id, cancellationToken);

        cycle.Rename(name);

        await performance.SaveAsync(cancellationToken);
    }

    public async Task CloseCycleAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var cycle = await RequiredCycle(id, cancellationToken);

        cycle.Close(clock.Now);

        await performance.SaveAsync(cancellationToken);
    }

    public async Task ReopenCycleAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var cycle = await RequiredCycle(id, cancellationToken);

        cycle.Reopen();

        await performance.SaveAsync(cancellationToken);
    }

    public async Task ExcludeAsync(
        Guid cycleId, Guid employeeId, CancellationToken cancellationToken = default)
    {
        var cycle = await RequiredCycle(cycleId, cancellationToken);

        cycle.Exclude(employeeId);

        await performance.SaveAsync(cancellationToken);
    }

    public Task<List<Goal>> GoalsForAsync(
        Guid employeeId, CancellationToken cancellationToken = default) =>
        performance.GoalsForAsync(employeeId, cancellationToken);

    public Task<Goal?> GoalAsync(Guid id, CancellationToken cancellationToken = default) =>
        performance.FindGoalAsync(id, cancellationToken);

    public Task<List<Goal>> OverdueAsync(
        IReadOnlyCollection<Guid> employeeIds, CancellationToken cancellationToken = default) =>
        performance.OverdueAsync(employeeIds, clock.Today, cancellationToken);

    public Task<List<ReviewCycle>> CyclesAsync(CancellationToken cancellationToken = default) =>
        performance.CyclesAsync(cancellationToken);

    public Task<List<ReviewCycle>> CyclesForAsync(
        Guid employeeId, CancellationToken cancellationToken = default) =>
        performance.CyclesForAsync(employeeId, cancellationToken);

    public Task<ReviewCycle?> CycleAsync(Guid id, CancellationToken cancellationToken = default) =>
        performance.FindCycleAsync(id, cancellationToken);

    private async Task<Goal> RequiredGoal(Guid id, CancellationToken cancellationToken) =>
        await performance.FindGoalAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("That goal is not on file.");

    private async Task<ReviewCycle> RequiredCycle(Guid id, CancellationToken cancellationToken) =>
        await performance.FindCycleAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("That review cycle does not exist.");
}
