using JiranisokoTech.Domain.Work;

namespace JiranisokoTech.Application.Work;

public interface IWorkRepository
{
    Task<WorkItem?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Project?> FindProjectAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> CodeTakenAsync(
        string code, Guid? exceptProjectId = null, CancellationToken cancellationToken = default);

    /// <summary>How many items under this project are neither done nor cancelled.</summary>
    Task<int> OpenItemCountAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>Open work assigned to this person, for handing on when they leave.</summary>
    Task<List<WorkItem>> OpenWorkForAsync(
        Guid employeeId, CancellationToken cancellationToken = default);

    void Add(WorkItem item);

    void Add(Project project);

    Task SaveAsync(CancellationToken cancellationToken = default);
}
