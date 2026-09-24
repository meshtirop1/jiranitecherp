using JiranisokoTech.Application.Work;
using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Work;

/// <summary>The sprint, backlog, hierarchy and dependency reads and writes.</summary>
public sealed class PlanningRepository(AppDbContext database) : IPlanningRepository
{
    public Task<Sprint?> FindSprintAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.Sprints.FirstOrDefaultAsync(one => one.Id == id, cancellationToken);

    /// <remarks>
    /// Running first, then what is planned, then what is over — which is the order somebody reads
    /// this in, because the only sprint anybody is working in is at the top.
    /// </remarks>
    public Task<List<Sprint>> SprintsAsync(CancellationToken cancellationToken = default) =>
        database.Sprints
            .AsNoTracking()
            .OrderBy(one => one.State == SprintState.Running ? 0
                : one.State == SprintState.Planned ? 1 : 2)
            .ThenByDescending(one => one.Starts)
            .ToListAsync(cancellationToken);

    public Task<Sprint?> RunningAsync(CancellationToken cancellationToken = default) =>
        database.Sprints.FirstOrDefaultAsync(
            one => one.State == SprintState.Running, cancellationToken);

    public Task<List<WorkItem>> InSprintAsync(
        Guid sprintId, CancellationToken cancellationToken = default) =>
        Full(database.WorkItems)
            .Where(one => one.SprintId == sprintId)
            .OrderBy(one => one.Status)
            .ThenBy(one => one.Priority)
            .ThenBy(one => one.Number)
            .ToListAsync(cancellationToken);

    public Task<List<WorkItem>> BacklogAsync(CancellationToken cancellationToken = default) =>
        Full(database.WorkItems.AsNoTracking())
            .Where(one => one.SprintId == null)
            .Where(one => one.Status != WorkItemStatus.Done
                && one.Status != WorkItemStatus.Cancelled
                && one.Status != WorkItemStatus.Deployed)
            /*
             * Biggest first, then by priority, then by number. An epic at the top of a backlog is
             * a heading; a backlog sorted only by priority scatters the pieces of one epic
             * through the list and nobody can see what is actually being planned.
             */
            .OrderBy(one => one.Kind)
            .ThenBy(one => one.Priority)
            .ThenBy(one => one.Number)
            .ToListAsync(cancellationToken);

    public Task<List<WorkItem>> ChildrenOfAsync(
        Guid parentId, CancellationToken cancellationToken = default) =>
        Full(database.WorkItems.AsNoTracking())
            .Where(one => one.ParentId == parentId)
            .OrderBy(one => one.Kind)
            .ThenBy(one => one.Number)
            .ToListAsync(cancellationToken);

    public Task<Dictionary<Guid, Guid?>> ParentsAsync(
        CancellationToken cancellationToken = default) =>
        database.WorkItems
            .AsNoTracking()
            .Select(one => new { one.Id, one.ParentId })
            .ToDictionaryAsync(one => one.Id, one => one.ParentId, cancellationToken);

    public Task<List<WorkItemLink>> LinksForAsync(
        Guid workItemId, CancellationToken cancellationToken = default) =>
        database.WorkItemLinks
            .AsNoTracking()
            .Where(one => one.BlockerId == workItemId || one.BlockedId == workItemId)
            .ToListAsync(cancellationToken);

    public Task<List<WorkItemLink>> AllLinksAsync(CancellationToken cancellationToken = default) =>
        database.WorkItemLinks.ToListAsync(cancellationToken);

    public Task<List<WorkItem>> ByIdsAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
        ids.Count == 0
            ? Task.FromResult(new List<WorkItem>())
            : database.WorkItems
                .AsNoTracking()
                .Where(one => ids.Contains(one.Id))
                .OrderBy(one => one.Number)
                .ToListAsync(cancellationToken);

    public void Add(Sprint sprint) => database.Sprints.Add(sprint);

    public void Add(WorkItemLink link) => database.WorkItemLinks.Add(link);

    public void Remove(WorkItemLink link) => database.WorkItemLinks.Remove(link);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        database.SaveChangesAsync(cancellationToken);

    /// <summary>
    /// A work item with the parts a board draws.
    /// </summary>
    /// <remarks>
    /// Labels and the done-list, because both appear on a card: a count of what is left and the
    /// words somebody filters by. Comments are not included — a board does not show them, and a
    /// thread of fifty on one card would be loaded for every row of the list.
    /// </remarks>
    private static IQueryable<WorkItem> Full(IQueryable<WorkItem> query) =>
        query.Include(one => one.Labels).Include(one => one.DoneWhen);
}
