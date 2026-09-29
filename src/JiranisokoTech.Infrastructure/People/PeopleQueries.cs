using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.People;

/// <summary>
/// The reads the People screens do, kept out of the screens.
/// </summary>
/// <remarks>
/// Separate from the service on purpose. Commands go through
/// <c>PeopleService</c> because they carry rules; reads carry none, and routing
/// them through the same place would mean a service method per column anybody
/// ever wanted to sort by.
///
/// Every query here is read-only and projects into a record. Handing a page a
/// tracked entity invites it to change one and wonder why nothing saved.
/// </remarks>
public sealed class PeopleQueries(AppDbContext database)
{
    public async Task<List<PersonRow>> RosterAsync(
        Guid? departmentId = null,
        EmploymentStatus? status = null,
        string? search = null,
        Reach? within = null,
        CancellationToken cancellationToken = default)
    {
        var query = database.Employees.AsNoTracking();

        /*
         * Narrowed to the departments the reader may see, when one was given.
         *
         * Optional, and that is a compromise worth naming. Every internal caller here
         * loads the roster to populate a dropdown of people who could take some work, and
         * narrowing those would make somebody unassignable because of who was looking at
         * the screen. So the limit is applied where a roster is being read, and not where
         * it is being used as a list of staff.
         *
         * Somebody who reaches no department sees nobody, which is why Reach.Nothing exists
         * as a value distinct from an empty limit — an empty set meaning "no limit" is how
         * a narrowing silently becomes a widening.
         */
        if (within is { IsEverything: false } limit)
        {
            query = limit.IsNothing
                ? query.Where(employee => false)
                : limit.Apply(query, employee => employee.DepartmentId ?? Guid.Empty);
        }

        if (departmentId is { } department)
        {
            query = query.Where(employee => employee.DepartmentId == department);
        }

        if (status is { } state)
        {
            query = query.Where(employee => employee.Status == state);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();

            query = query.Where(employee =>
                EF.Functions.Like(employee.FullName, $"%{term}%")
                || (employee.JobTitle != null && EF.Functions.Like(employee.JobTitle, $"%{term}%")));
        }

        // Left-hand join by hand rather than a navigation property: the entities
        // deliberately have none, so that the domain stays a set of rules rather
        // than an object graph that loads half the firm by accident.
        var rows = await query
            .OrderBy(employee => employee.FullName)
            .Select(employee => new
            {
                employee.Id,
                employee.FullName,
                employee.JobTitle,
                employee.Status,
                employee.StartsOn,
                employee.DepartmentId,
                employee.ReportsToId,
                employee.AccountId,
                employee.WeeklyCapacityHours,
            })
            .ToListAsync(cancellationToken);

        var names = await NamesAsync(cancellationToken);
        var departments = await DepartmentNamesAsync(cancellationToken);

        return rows.Select(row => new PersonRow(
            row.Id,
            row.FullName,
            row.JobTitle,
            row.Status,
            row.StartsOn,
            row.DepartmentId,
            row.DepartmentId is { } id ? departments.GetValueOrDefault(id) : null,
            row.ReportsToId,
            row.ReportsToId is { } manager ? names.GetValueOrDefault(manager) : null,
            row.AccountId is not null,
            row.WeeklyCapacityHours)).ToList();
    }

    public async Task<PersonRow?> PersonAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var roster = await RosterAsync(cancellationToken: cancellationToken);

        return roster.FirstOrDefault(person => person.Id == id);
    }

    /// <summary>
    /// The staff record behind a sign-in, if there is one.
    /// </summary>
    /// <remarks>
    /// An account and an employee are separate on purpose, so this can be null:
    /// a service account, or somebody whose record has not been linked yet. The
    /// callers that need a person say so plainly rather than inventing one.
    /// </remarks>
    public Task<Guid?> EmployeeForAccountAsync(
        Guid accountId, CancellationToken cancellationToken = default) =>
        database.Employees
            .AsNoTracking()
            .Where(employee => employee.AccountId == accountId)
            .Select(employee => (Guid?)employee.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<Dictionary<Guid, string>> NamesAsync(CancellationToken cancellationToken = default) =>
        database.Employees
            .AsNoTracking()
            .ToDictionaryAsync(employee => employee.Id, employee => employee.FullName, cancellationToken);

    public Task<Dictionary<Guid, string>> DepartmentNamesAsync(
        CancellationToken cancellationToken = default) =>
        database.Departments
            .AsNoTracking()
            .ToDictionaryAsync(
                department => department.Id, department => department.Name, cancellationToken);

    /// <summary>
    /// Which department each person sits in.
    /// </summary>
    /// <remarks>
    /// The whole staff list in one dictionary, for the same reason the reporting lines are
    /// loaded that way: this is a firm of tens of people and the caller needs every row at
    /// once. The teams page uses it to say which departments a team spans, which is the fact
    /// that makes a team visibly not a department.
    /// </remarks>
    public Task<Dictionary<Guid, Guid?>> DepartmentOfEachAsync(
        CancellationToken cancellationToken = default) =>
        database.Employees
            .AsNoTracking()
            .Select(employee => new { employee.Id, employee.DepartmentId })
            .ToDictionaryAsync(one => one.Id, one => one.DepartmentId, cancellationToken);

    public async Task<List<DepartmentRow>> DepartmentsAsync(
        bool includeClosed = true, CancellationToken cancellationToken = default)
    {
        var query = database.Departments.AsNoTracking();

        if (!includeClosed)
        {
            query = query.Where(department => department.IsActive);
        }

        var departments = await query
            .OrderBy(department => department.Name)
            .Select(department => new
            {
                department.Id,
                department.Name,
                department.Slug,
                department.Description,
                department.IsActive,
                department.HeadEmployeeId,
            })
            .ToListAsync(cancellationToken);

        var headcount = await database.Employees
            .AsNoTracking()
            .Where(employee => employee.DepartmentId != null
                && employee.Status != EmploymentStatus.Left)
            .GroupBy(employee => employee.DepartmentId!.Value)
            .Select(group => new { DepartmentId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.DepartmentId, row => row.Count, cancellationToken);

        var names = await NamesAsync(cancellationToken);

        return departments.Select(department => new DepartmentRow(
            department.Id,
            department.Name,
            department.Slug,
            department.Description,
            department.IsActive,
            department.HeadEmployeeId,
            department.HeadEmployeeId is { } head ? names.GetValueOrDefault(head) : null,
            headcount.GetValueOrDefault(department.Id))).ToList();
    }

    /// <summary>
    /// The firm as a tree, for drawing.
    /// </summary>
    /// <remarks>
    /// People who have left are left out: an organisation chart is a picture of
    /// who is here now, and a leaver drawn in it is a person somebody will try
    /// to assign work to.
    ///
    /// Anybody whose manager is missing from this set — because that manager has
    /// left — is treated as a root rather than dropped, so nobody vanishes from
    /// the picture because of somebody else's departure.
    /// </remarks>
    public async Task<List<ChartNode>> ChartAsync(CancellationToken cancellationToken = default)
    {
        var roster = await RosterAsync(cancellationToken: cancellationToken);
        var here = roster.Where(person => person.Status != EmploymentStatus.Left).ToList();
        var present = here.Select(person => person.Id).ToHashSet();

        var children = here
            .Where(person => person.ReportsToId is { } manager && present.Contains(manager))
            .GroupBy(person => person.ReportsToId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());

        var roots = here
            .Where(person => person.ReportsToId is not { } manager || !present.Contains(manager))
            .ToList();

        return roots.Select(root => Build(root, children)).ToList();
    }

    /// <summary>
    /// Who answers for this person: their manager, or the head of their department.
    /// </summary>
    /// <remarks>
    /// The fallback is not a convenience. A reporting line is often unset for exactly the people
    /// this question gets asked about — somebody hired last week, somebody moved between teams —
    /// and "tell the manager" reaching nobody on those occasions would make every escalation
    /// built on it silently do nothing.
    ///
    /// Never the person themselves. A head of department whose own promise was missed must not be
    /// escalated to about it, because a notice from yourself about yourself is the one somebody
    /// stops reading first.
    ///
    /// Lives here rather than beside its first caller because it has two: section 31's automation
    /// action that notifies a manager, and section 26's sweep for missed promises. Two copies of
    /// "who is this person's manager" is two answers the day somebody changes one.
    /// </remarks>
    public async Task<List<Guid>> WhoAnswersForAsync(
        Guid person, CancellationToken cancellationToken = default)
    {
        var employee = await database.Employees
            .AsNoTracking()
            .Where(one => one.Id == person)
            .Select(one => new { one.ReportsToId, one.DepartmentId })
            .FirstOrDefaultAsync(cancellationToken);

        if (employee?.ReportsToId is { } manager && manager != person)
        {
            return [manager];
        }

        if (employee?.DepartmentId is not { } department)
        {
            return [];
        }

        return await database.Departments
            .AsNoTracking()
            .Where(one => one.Id == department
                && one.HeadEmployeeId != null
                && one.HeadEmployeeId != person)
            .Select(one => one.HeadEmployeeId!.Value)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Everybody who heads a department, for the occasions that have no one person to tell.
    /// </summary>
    /// <remarks>
    /// The fallback of last resort, and it is deliberately the smallest set that certainly
    /// contains somebody who can act. There is no "support lead" anywhere in this schema and
    /// inventing one would be a setting nobody configures — so an unanswered request with nobody
    /// on it reaches the people who answer for the firm's departments, because it is a failure of
    /// the desk rather than of a person.
    ///
    /// Active departments only: a head still recorded against a department the firm has closed is
    /// not somebody with anything to do about today's queue.
    /// </remarks>
    public Task<List<Guid>> WhoHeadsADepartmentAsync(
        CancellationToken cancellationToken = default) =>
        database.Departments
            .AsNoTracking()
            .Where(one => one.IsActive && one.HeadEmployeeId != null)
            .Select(one => one.HeadEmployeeId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

    private static ChartNode Build(PersonRow person, Dictionary<Guid, List<PersonRow>> children) =>
        new(person, children.TryGetValue(person.Id, out var reports)
            ? reports.Select(report => Build(report, children)).ToList()
            : []);
}

public sealed record PersonRow(
    Guid Id,
    string FullName,
    string? JobTitle,
    EmploymentStatus Status,
    DateOnly StartsOn,
    Guid? DepartmentId,
    string? DepartmentName,
    Guid? ReportsToId,
    string? ReportsToName,
    bool HasAccount,
    int WeeklyCapacityHours);

public sealed record DepartmentRow(
    Guid Id,
    string Name,
    string Slug,
    string? Description,
    bool IsActive,
    Guid? HeadEmployeeId,
    string? HeadName,
    int Headcount);

public sealed record ChartNode(PersonRow Person, IReadOnlyList<ChartNode> Reports);
