using JiranisokoTech.Domain.Work;

namespace JiranisokoTech.Application.Work;

public interface IWorkRepository
{
    Task<WorkItem?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The work item somebody wrote in a branch name.</summary>
    Task<WorkItem?> ByNumberAsync(int number, CancellationToken cancellationToken = default);

    /// <summary>The highest number issued so far, or zero.</summary>
    /// <remarks>
    /// Work is numbered so that a person can write the reference in a branch
    /// name while thinking about something else, which is the only way a commit
    /// gets tied back to the work it belongs to. Asked of the database because
    /// that is where the answer is.
    /// </remarks>
    Task<int> LastNumberAsync(CancellationToken cancellationToken = default);

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
