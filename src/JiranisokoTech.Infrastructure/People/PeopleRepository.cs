using JiranisokoTech.Application.People;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.People;

/// <summary>The People queries, over the one context.</summary>
public sealed class PeopleRepository(AppDbContext database) : IPeopleRepository
{
    public Task<Employee?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.Employees.FirstOrDefaultAsync(employee => employee.Id == id, cancellationToken);

    public Task<Department?> FindDepartmentAsync(
        Guid id, CancellationToken cancellationToken = default) =>
        database.Departments.FirstOrDefaultAsync(
            department => department.Id == id, cancellationToken);

    public Task<bool> DepartmentExistsAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.Departments.AnyAsync(department => department.Id == id, cancellationToken);

    public Task<bool> SlugTakenAsync(
        string slug, Guid? exceptDepartmentId = null, CancellationToken cancellationToken = default) =>
        database.Departments.AnyAsync(
            department => department.Slug == slug && department.Id != exceptDepartmentId,
            cancellationToken);

    public Task<Dictionary<Guid, Guid?>> ReportingLinesAsync(
        CancellationToken cancellationToken = default) =>
        database.Employees
            .AsNoTracking()
            .Select(employee => new { employee.Id, employee.ReportsToId })
            .ToDictionaryAsync(line => line.Id, line => line.ReportsToId, cancellationToken);

    public Task<List<Employee>> DirectReportsAsync(
        Guid managerId, CancellationToken cancellationToken = default) =>
        database.Employees
            .Where(employee => employee.ReportsToId == managerId)
            .ToListAsync(cancellationToken);

    public Task<List<Department>> HeadedByAsync(
        Guid employeeId, CancellationToken cancellationToken = default) =>
        database.Departments
            .Where(department => department.HeadEmployeeId == employeeId)
            .ToListAsync(cancellationToken);

    public void Add(Employee employee) => database.Employees.Add(employee);

    public void Add(Department department) => database.Departments.Add(department);

    public Task<Offboarding?> OffboardingForAsync(
        Guid employeeId, CancellationToken cancellationToken = default) =>
        database.Offboardings.FirstOrDefaultAsync(
            one => one.EmployeeId == employeeId, cancellationToken);

    public Task<Offboarding?> FindOffboardingAsync(
        Guid id, CancellationToken cancellationToken = default) =>
        database.Offboardings.FirstOrDefaultAsync(one => one.Id == id, cancellationToken);

    public Task<List<Offboarding>> UnfinishedOffboardingsAsync(
        CancellationToken cancellationToken = default) =>
        database.Offboardings
            .Where(one => one.CompletedAt == null)
            .OrderBy(one => one.LeavingOn)
            .ToListAsync(cancellationToken);

    /// <remarks>
    /// The steps and the equipment come with it, always — every write to a checklist is a write
    /// to one of those two collections, and an owned collection EF never loaded is one it
    /// happily replaces with nothing.
    /// </remarks>
    public Task<Onboarding?> OnboardingForAsync(
        Guid employeeId, CancellationToken cancellationToken = default) =>
        database.Onboardings
            .Include(one => one.Steps)
            .Include(one => one.Issued)
            .FirstOrDefaultAsync(one => one.EmployeeId == employeeId, cancellationToken);

    public Task<List<Onboarding>> OnboardingsAsync(
        CancellationToken cancellationToken = default) =>
        database.Onboardings
            .AsNoTracking()
            .Include(one => one.Steps)
            .Where(one => one.CompletedAt == null)
            .OrderBy(one => one.StartsOn)
            .ToListAsync(cancellationToken);

    public void Add(Onboarding onboarding) => database.Onboardings.Add(onboarding);

    public void Add(Offboarding offboarding) => database.Offboardings.Add(offboarding);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        database.SaveChangesAsync(cancellationToken);
}
