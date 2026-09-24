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

    /// <summary>
    /// Whose absence somebody may see on the calendar.
    /// </summary>
    /// <remarks>
    /// Section 33. Composed rather than copied: the department is already the unit of leave
    /// authority — the teams row says "a department is where somebody sits, deciding who they
    /// answer to and who signs off their leave" — so the department reach <i>is</i> the leave
    /// reach by this codebase's own definition. Writing a second rule here would be a second
    /// answer to one question, and the two would disagree the first time either changed.
    ///
    /// Without leave.view_all somebody reaches nobody, not even themselves. Their own leave is on
    /// the calendar unconditionally and does not come through here — a reach is about other
    /// people, and folding "yourself" into it would make every caller wonder whether it had.
    /// </remarks>
    public async Task<Reach> AbsenceAsync(
        IReadOnlySet<string> permissions,
        Guid? employeeId,
        CancellationToken cancellationToken = default) =>
        permissions.Contains(Permissions.LeaveViewAll)
            ? await DepartmentsAsync(permissions, employeeId, cancellationToken)
            : Reach.Nothing;

    /// <summary>
    /// Whose goals and reviews somebody may open.
    /// </summary>
    /// <remarks>
    /// Their own, always, plus everybody below them in the reporting line — and that is a
    /// different rule from the roster's, on purpose. The roster narrows by department, because a
    /// staff list is about the shape of the firm; performance narrows by who answers to whom,
    /// because an appraisal is a line-management relationship. A head of engineering who is not
    /// in somebody's chain has no business in their review, and the two rules disagree about that
    /// case precisely because they should.
    ///
    /// The whole chain rather than direct reports only, for the reason the department reach gives
    /// for going down the tree: a head with three team leads under them answers for everybody
    /// below those leads, and a reach that stopped at the first level would hide most of their own
    /// organisation.
    ///
    /// Somebody with no staff record reaches nothing, not even themselves — there is nobody to be.
    /// </remarks>
    public async Task<IReadOnlySet<Guid>> PerformanceAsync(
        IReadOnlySet<string> permissions,
        Guid? employeeId,
        CancellationToken cancellationToken = default)
    {
        /*
         * Everybody, for HR. Unlike the other two reaches this one returns a set of people rather
         * than a Reach, so "everything" has to be enumerated — which is fine at this size and is
         * the honest answer: the caller is going to list them.
         */
        if (permissions.Contains(Permissions.GoalsViewAll))
        {
            return (await database.Employees
                .AsNoTracking()
                .Select(employee => employee.Id)
                .ToListAsync(cancellationToken))
                .ToHashSet();
        }

        if (employeeId is not { } person)
        {
            return new HashSet<Guid>();
        }

        var reach = new HashSet<Guid> { person };

        if (!permissions.Contains(Permissions.GoalsManage))
        {
            return reach;
        }

        var lines = await database.Employees
            .AsNoTracking()
            .Select(employee => new { employee.Id, employee.ReportsToId })
            .ToListAsync(cancellationToken);

        /*
         * Walked here rather than asked of the database one level at a time, the same decision
         * IPeopleRepository.ReportingLinesAsync explains: this is a firm of tens of people, and a
         * round trip per level of an org chart is a query count that depends on how tall the firm
         * is. When that stops being true this becomes a recursive query.
         *
         * The loop terminates on a set that stopped growing rather than on a depth, so a cycle in
         * the reporting lines cannot hang it. Nothing should be able to create one — the service
         * refuses a loop — but a reach that hangs for ever is a worse way to find out.
         */
        var added = true;

        while (added)
        {
            added = false;

            foreach (var line in lines)
            {
                if (line.ReportsToId is { } above && reach.Contains(above) && reach.Add(line.Id))
                {
                    added = true;
                }
            }
        }

        return reach;
    }
}
