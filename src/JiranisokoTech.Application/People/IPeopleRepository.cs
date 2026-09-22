using JiranisokoTech.Domain.People;

namespace JiranisokoTech.Application.People;

/// <summary>
/// What the People rules need from storage, and nothing more.
/// </summary>
/// <remarks>
/// Declared here rather than in the infrastructure so the service can be tested
/// against a dictionary, and so the rules stay readable without EF Core in the
/// way. The implementation is one class over the DbContext.
/// </remarks>
public interface IPeopleRepository
{
    Task<Employee?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Department?> FindDepartmentAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> DepartmentExistsAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> SlugTakenAsync(
        string slug, Guid? exceptDepartmentId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Who everybody currently answers to.
    /// </summary>
    /// <remarks>
    /// The whole firm in one dictionary, because the loop check has to walk a
    /// chain and doing that one query at a time is a round trip per step. This
    /// is a company of tens of people, not tens of thousands; when that stops
    /// being true, the walk becomes a recursive query and this method goes.
    /// </remarks>
    Task<Dictionary<Guid, Guid?>> ReportingLinesAsync(CancellationToken cancellationToken = default);

    /// <summary>Everyone who answers to this person directly.</summary>
    Task<List<Employee>> DirectReportsAsync(Guid managerId, CancellationToken cancellationToken = default);

    /// <summary>Departments this person is head of. Normally none or one.</summary>
    Task<List<Department>> HeadedByAsync(Guid employeeId, CancellationToken cancellationToken = default);

    void Add(Employee employee);

    void Add(Department department);

    Task SaveAsync(CancellationToken cancellationToken = default);
}
