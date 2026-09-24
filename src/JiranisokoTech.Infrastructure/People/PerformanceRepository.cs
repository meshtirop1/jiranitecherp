using JiranisokoTech.Application.People;
using JiranisokoTech.Domain.Performance;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.People;

/// <summary>The goal and review reads and writes.</summary>
public sealed class PerformanceRepository(AppDbContext database) : IPerformanceRepository
{
    public Task<Goal?> FindGoalAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.Goals
            .Include(one => one.Notes)
            .FirstOrDefaultAsync(one => one.Id == id, cancellationToken);

    /// <remarks>
    /// Open ones first and then by their date, because the question this list answers is "what am
    /// I meant to be doing" — and last year's closed goals are the answer to a different one.
    /// </remarks>
    public Task<List<Goal>> GoalsForAsync(
        Guid employeeId, CancellationToken cancellationToken = default) =>
        database.Goals
            .AsNoTracking()
            .Include(one => one.Notes)
            .Where(one => one.ForEmployeeId == employeeId)
            .OrderBy(one => one.Outcome != null)
            .ThenBy(one => one.To)
            .ThenBy(one => one.Title)
            .ToListAsync(cancellationToken);

    /// <remarks>
    /// An empty set of people means nobody rather than everybody. The same trap
    /// <c>Reach.Nothing</c> exists for: a caller that narrowed to nothing and got the firm back
    /// would be a narrowing that had silently become a widening.
    /// </remarks>
    public Task<List<Goal>> OverdueAsync(
        IReadOnlyCollection<Guid> employeeIds,
        DateOnly today,
        CancellationToken cancellationToken = default) =>
        employeeIds.Count == 0
            ? Task.FromResult(new List<Goal>())
            : database.Goals
                .AsNoTracking()
                .Where(one => employeeIds.Contains(one.ForEmployeeId))
                .Where(one => one.Outcome == null && one.To < today)
                .OrderBy(one => one.To)
                .ToListAsync(cancellationToken);

    public Task<ReviewCycle?> FindCycleAsync(
        Guid id, CancellationToken cancellationToken = default) =>
        database.ReviewCycles
            .Include(one => one.Reviews)
            .FirstOrDefaultAsync(one => one.Id == id, cancellationToken);

    public Task<List<ReviewCycle>> CyclesAsync(CancellationToken cancellationToken = default) =>
        database.ReviewCycles
            .AsNoTracking()
            .Include(one => one.Reviews)
            .OrderByDescending(one => one.IsClosed == false)
            .ThenByDescending(one => one.To)
            .ToListAsync(cancellationToken);

    public Task<List<ReviewCycle>> CyclesForAsync(
        Guid employeeId, CancellationToken cancellationToken = default) =>
        database.ReviewCycles
            .AsNoTracking()
            .Include(one => one.Reviews)
            .Where(one => one.Reviews.Any(review => review.EmployeeId == employeeId))
            .OrderByDescending(one => one.To)
            .ToListAsync(cancellationToken);

    public void Add(Goal goal) => database.Goals.Add(goal);

    public void Add(ReviewCycle cycle) => database.ReviewCycles.Add(cycle);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        database.SaveChangesAsync(cancellationToken);
}
