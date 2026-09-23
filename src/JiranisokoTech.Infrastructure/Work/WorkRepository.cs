using JiranisokoTech.Application.Work;
using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Work;

public sealed class WorkRepository(AppDbContext database) : IWorkRepository
{
    public Task<WorkItem?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.WorkItems.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

    public Task<WorkItem?> ByNumberAsync(
        int number, CancellationToken cancellationToken = default) =>
        database.WorkItems.FirstOrDefaultAsync(item => item.Number == number, cancellationToken);

    public async Task<int> LastNumberAsync(CancellationToken cancellationToken = default) =>
        await database.WorkItems
            .AsNoTracking()
            .Select(item => (int?)item.Number)
            .MaxAsync(cancellationToken) ?? 0;

    public Task<Project?> FindProjectAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.Projects.FirstOrDefaultAsync(project => project.Id == id, cancellationToken);

    public Task<bool> CodeTakenAsync(
        string code, Guid? exceptProjectId = null, CancellationToken cancellationToken = default) =>
        database.Projects.AnyAsync(
            project => project.Code == code && project.Id != exceptProjectId, cancellationToken);

    public Task<int> OpenItemCountAsync(
        Guid projectId, CancellationToken cancellationToken = default) =>
        database.WorkItems.CountAsync(
            item => item.ProjectId == projectId
                && !WorkItem.Finished.Contains(item.Status),
            cancellationToken);

    /// <summary>
    /// The work a leaver still has to hand over.
    /// </summary>
    /// <remarks>
    /// Asks the domain which states are finished rather than naming two of them,
    /// so that a released item is not taken off somebody on their way out. It
    /// would change the record of who delivered it, for no gain: nobody has to
    /// pick it up.
    /// </remarks>
    public Task<List<WorkItem>> OpenWorkForAsync(
        Guid employeeId, CancellationToken cancellationToken = default) =>
        database.WorkItems
            .Where(item => item.AssigneeId == employeeId
                && !WorkItem.Finished.Contains(item.Status))
            .ToListAsync(cancellationToken);

    public void Add(WorkItem item) => database.WorkItems.Add(item);

    public void Add(Project project) => database.Projects.Add(project);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        database.SaveChangesAsync(cancellationToken);
}
