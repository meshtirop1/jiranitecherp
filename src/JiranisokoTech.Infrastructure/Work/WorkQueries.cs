using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Work;

/// <summary>The reads the work screens do, kept out of the screens.</summary>
public sealed class WorkQueries(AppDbContext database)
{
    /// <summary>
    /// One work item, by its identifier.
    /// </summary>
    /// <remarks>
    /// Here because the screen that shows one used to read every work item in the system
    /// and pick its own out of the list. That is invisible on a laptop with forty rows and
    /// it is three quarters of a second and eighty thousand objects at a hundred and twenty
    /// thousand — per view, of one item. Found by the scale check, which did not test this
    /// path: it measured the list, and reading who called the list found this.
    /// </remarks>
    public async Task<WorkItemRow?> ItemAsync(
        Guid id, CancellationToken cancellationToken = default) =>
        (await ItemsAsync(id: id, cancellationToken: cancellationToken)).FirstOrDefault();

    /// <summary>How many items match, for a screen that pages them.</summary>
    public Task<int> CountItemsAsync(
        Guid? projectId = null,
        Guid? assigneeId = null,
        bool openOnly = false,
        CancellationToken cancellationToken = default) =>
        Narrow(database.WorkItems.AsNoTracking(), null, projectId, assigneeId, openOnly)
            .CountAsync(cancellationToken);

    /// <param name="take">
    /// How many at most. Null means all of them, which is right for a project's own board
    /// and wrong for the whole firm's — see the note on the skip and take in BusinessQueries
    /// .InvoicesAsync for why this is a parameter rather than a cap applied here.
    /// </param>
    public async Task<List<WorkItemRow>> ItemsAsync(
        Guid? projectId = null,
        Guid? assigneeId = null,
        bool openOnly = false,
        Guid? id = null,
        int skip = 0,
        int? take = null,
        CancellationToken cancellationToken = default)
    {
        var query = Narrow(database.WorkItems.AsNoTracking(), id, projectId, assigneeId, openOnly);

        var rows = await query
            // Most pressing first: a board sorted by when a row was written is a
            // board nobody reads twice.
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.DueOn == null)
            .ThenBy(item => item.DueOn)
            .ThenBy(item => item.Title)
            .Skip(skip)
            .Take(take ?? int.MaxValue)
            .Select(item => new
            {
                item.Id,
                item.Number,
                item.Title,
                item.Status,
                item.Priority,
                item.ProjectId,
                item.AssigneeId,
                item.DueOn,
                item.EstimateMinutes,
                item.BlockedReason,
                item.Kind,
            })
            .ToListAsync(cancellationToken);

        var people = await database.Employees
            .AsNoTracking()
            .ToDictionaryAsync(person => person.Id, person => person.FullName, cancellationToken);

        var projects = await database.Projects
            .AsNoTracking()
            .ToDictionaryAsync(project => project.Id, project => project.Name, cancellationToken);

        return rows.Select(row => new WorkItemRow(
            row.Id,
            row.Number,
            row.Title,
            row.Status,
            row.Priority,
            row.ProjectId,
            row.ProjectId is { } project ? projects.GetValueOrDefault(project) : null,
            row.AssigneeId,
            row.AssigneeId is { } assignee ? people.GetValueOrDefault(assignee) : null,
            row.DueOn,
            row.EstimateMinutes,
            row.BlockedReason,
            row.Kind)).ToList();
    }

    /// <summary>
    /// The filtering, in one place, because a count and a page have to agree.
    /// </summary>
    /// <remarks>
    /// Two copies of these four conditions is how a screen ends up saying "1 to 50 of 200"
    /// over a list of eighty — which nobody reports as a bug, they just stop trusting the
    /// number.
    /// </remarks>
    private static IQueryable<WorkItem> Narrow(
        IQueryable<WorkItem> query,
        Guid? id,
        Guid? projectId,
        Guid? assigneeId,
        bool openOnly)
    {
        if (id is { } one)
        {
            query = query.Where(item => item.Id == one);
        }

        if (projectId is { } project)
        {
            query = query.Where(item => item.ProjectId == project);
        }

        if (assigneeId is { } assignee)
        {
            query = query.Where(item => item.AssigneeId == assignee);
        }

        if (openOnly)
        {
            // The domain's list of finished states rather than a pair of comparisons
            // written out here. The pair was the bug waiting to happen: when a seventh
            // state arrived, every copy of it went on reporting released work as open.
            query = query.Where(item => !WorkItem.Finished.Contains(item.Status));
        }

        return query;
    }

    public async Task<List<ProjectRow>> ProjectsAsync(
        bool runningOnly = false, CancellationToken cancellationToken = default)
    {
        var query = database.Projects.AsNoTracking();

        if (runningOnly)
        {
            query = query.Where(project =>
                project.Status != ProjectStatus.Delivered
                && project.Status != ProjectStatus.Cancelled);
        }

        var projects = await query
            .OrderBy(project => project.Name)
            .Select(project => new
            {
                project.Id,
                project.Name,
                project.Code,
                project.Status,
                project.DueOn,
                project.LeadId,
            })
            .ToListAsync(cancellationToken);

        var counts = await database.WorkItems
            .AsNoTracking()
            .Where(item => item.ProjectId != null)
            .GroupBy(item => item.ProjectId!.Value)
            .Select(group => new
            {
                ProjectId = group.Key,
                Open = group.Count(item => !WorkItem.Finished.Contains(item.Status)),
                Total = group.Count(),
            })
            .ToDictionaryAsync(row => row.ProjectId, row => row, cancellationToken);

        var people = await database.Employees
            .AsNoTracking()
            .ToDictionaryAsync(person => person.Id, person => person.FullName, cancellationToken);

        return projects.Select(project =>
        {
            var count = counts.GetValueOrDefault(project.Id);

            return new ProjectRow(
                project.Id,
                project.Name,
                project.Code,
                project.Status,
                project.DueOn,
                project.LeadId is { } lead ? people.GetValueOrDefault(lead) : null,
                count?.Open ?? 0,
                count?.Total ?? 0);
        }).ToList();
    }

    /// <summary>
    /// One board, grouped the way it is looked at.
    /// </summary>
    /// <remarks>
    /// Cancelled work is left out. It is kept in the table because "why did we
    /// not do that?" is a real question, but a board is about what is in front
    /// of people now.
    ///
    /// Deployed work is not left out, although it is just as finished. The
    /// release is the last thing that happens to a piece of work and the column
    /// is the only place anybody can see what went out this week; a head who has
    /// just released four things and sees no trace of them assumes the button
    /// did nothing.
    /// </remarks>
    public async Task<Dictionary<WorkItemStatus, List<WorkItemRow>>> BoardAsync(
        Guid? projectId = null,
        Guid? assigneeId = null,
        CancellationToken cancellationToken = default)
    {
        var items = await ItemsAsync(projectId, assigneeId, cancellationToken: cancellationToken);

        return Enum.GetValues<WorkItemStatus>()
            .Where(status => status != WorkItemStatus.Cancelled)
            .ToDictionary(
                status => status,
                status => items.Where(item => item.Status == status).ToList());
    }
}

public sealed record WorkItemRow(
    Guid Id,
    int Number,
    string Title,
    WorkItemStatus Status,
    Priority Priority,
    Guid? ProjectId,
    string? ProjectName,
    Guid? AssigneeId,
    string? AssigneeName,
    DateOnly? DueOn,
    int? EstimateMinutes,
    string? BlockedReason,
    WorkItemKind Kind = WorkItemKind.Task)
{
    /// <summary>The kind, as somebody would say it.</summary>
    public string Sized => WorkItem.Name(Kind);

    /// <summary>
    /// What a developer writes in a branch name.
    /// </summary>
    /// <remarks>
    /// On the row rather than only on the entity because it belongs on the
    /// screen: the reference is the one thing somebody has to carry from here
    /// into a branch name for their commits to come back attached to this work,
    /// and a number they cannot see is a link they will not make.
    /// </remarks>
    public string Reference => $"#{Number}";

    /// <summary>The estimate as somebody would say it, or nothing.</summary>
    public string? Estimate => EstimateMinutes switch
    {
        null => null,
        < 60 => $"{EstimateMinutes}m",
        _ when EstimateMinutes % 60 == 0 => $"{EstimateMinutes / 60}h",
        _ => $"{EstimateMinutes / 60}h {EstimateMinutes % 60}m",
    };
}

public sealed record ProjectRow(
    Guid Id,
    string Name,
    string Code,
    ProjectStatus Status,
    DateOnly? DueOn,
    string? LeadName,
    int OpenItems,
    int TotalItems);
