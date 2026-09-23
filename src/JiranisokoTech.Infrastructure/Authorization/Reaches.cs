using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Authorization;

/// <summary>
/// Works out how far somebody can see.
/// </summary>
/// <remarks>
/// One place, because the alternative is each screen deciding for itself what "the
/// projects you are on" means — and they would disagree. The search box already did: it
/// read `projects.view_member` as permission to find every project by name, because
/// returning nothing would have made the box useless for almost everybody. That is the
/// fault this class exists to remove rather than to hide.
/// </remarks>
public sealed class Reaches(AppDbContext database)
{
    /// <summary>
    /// The projects somebody may see.
    /// </summary>
    /// <remarks>
    /// Firm-wide for anybody holding projects.view_all. For everybody else it is the
    /// projects they are actually on, and "on" is defined here rather than left to
    /// interpretation: you lead it, or you are assigned work under it.
    ///
    /// That definition is deliberately about work rather than about a membership table.
    /// A table would be a second thing to keep current, and it would be wrong within a
    /// month — whereas somebody assigned a task on a project is on that project by any
    /// reading, and the board already knows.
    ///
    /// Somebody with no staff record reaches nothing. An account not linked to a person
    /// cannot be assigned work, so there is no honest set to give it, and Everything is
    /// the wrong default in a method whose whole job is narrowing.
    /// </remarks>
    public async Task<Reach> ProjectsAsync(
        IReadOnlySet<string> permissions,
        Guid? employeeId,
        CancellationToken cancellationToken = default)
    {
        if (permissions.Contains(Permissions.ProjectsViewAll))
        {
            return Reach.Everything;
        }

        if (!permissions.Contains(Permissions.ProjectsViewMember) || employeeId is not { } person)
        {
            return Reach.Nothing;
        }

        var led = await database.Projects
            .AsNoTracking()
            .Where(project => project.LeadId == person)
            .Select(project => project.Id)
            .ToListAsync(cancellationToken);

        var worked = await database.WorkItems
            .AsNoTracking()
            .Where(item => item.AssigneeId == person && item.ProjectId != null)
            .Select(item => item.ProjectId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        return Reach.LimitedTo(led.Concat(worked));
    }

    /// <summary>
    /// The departments somebody may see the people of.
    /// </summary>
    /// <remarks>
    /// Firm-wide for anybody holding employees.view_all — a new permission, because
    /// employees.view was already granted widely and narrowing it in place would have
    /// quietly taken the roster away from every delivery manager in the firm.
    ///
    /// For a department head it is their own department and everything under it. "Under"
    /// matters: a head of engineering with three team leads reporting to them heads one
    /// department and answers for people in several, and a reach that stopped at their own
    /// department would hide most of their own organisation from them.
    /// </remarks>
    public async Task<Reach> DepartmentsAsync(
        IReadOnlySet<string> permissions,
        Guid? employeeId,
        CancellationToken cancellationToken = default)
    {
        if (permissions.Contains(Permissions.EmployeesViewAll))
        {
            return Reach.Everything;
        }

        if (!permissions.Contains(Permissions.EmployeesView) || employeeId is not { } person)
        {
            return Reach.Nothing;
        }

        var headed = await database.Departments
            .AsNoTracking()
            .Where(department => department.HeadEmployeeId == person)
            .Select(department => department.Id)
            .ToListAsync(cancellationToken);

        /*
         * Plus whichever department they are in themselves. Somebody who heads nothing
         * still works somewhere, and a roster that showed them nobody — including
         * themselves — would be a screen with no use at all.
         */
        var own = await database.Employees
            .AsNoTracking()
            .Where(employee => employee.Id == person && employee.DepartmentId != null)
            .Select(employee => employee.DepartmentId!.Value)
            .ToListAsync(cancellationToken);

        return Reach.LimitedTo(headed.Concat(own));
    }
}
