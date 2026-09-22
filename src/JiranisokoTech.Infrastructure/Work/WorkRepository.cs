using JiranisokoTech.Application.Work;
using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Work;

public sealed class WorkRepository(AppDbContext database) : IWorkRepository
{
    public Task<WorkItem?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.WorkItems.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

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
                && item.Status != WorkItemStatus.Done
                && item.Status != WorkItemStatus.Cancelled,
            cancellationToken);

    public Task<List<WorkItem>> OpenWorkForAsync(
        Guid employeeId, CancellationToken cancellationToken = default) =>
        database.WorkItems
            .Where(item => item.AssigneeId == employeeId
                && item.Status != WorkItemStatus.Done
                && item.Status != WorkItemStatus.Cancelled)
            .ToListAsync(cancellationToken);

    public void Add(WorkItem item) => database.WorkItems.Add(item);

    public void Add(Project project) => database.Projects.Add(project);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        database.SaveChangesAsync(cancellationToken);
}
