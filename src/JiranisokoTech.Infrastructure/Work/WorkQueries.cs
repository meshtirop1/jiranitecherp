using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Work;

/// <summary>The reads the work screens do, kept out of the screens.</summary>
public sealed class WorkQueries(AppDbContext database)
{
    public async Task<List<WorkItemRow>> ItemsAsync(
        Guid? projectId = null,
        Guid? assigneeId = null,
        bool openOnly = false,
        CancellationToken cancellationToken = default)
    {
        var query = database.WorkItems.AsNoTracking();

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
            // The domain's list of finished states rather than a pair of
            // comparisons written out here. The pair was the bug waiting to
            // happen: when a seventh state arrived, every copy of it went on
            // reporting released work as open.
            query = query.Where(item => !WorkItem.Finished.Contains(item.Status));
        }

        var rows = await query
            // Most pressing first: a board sorted by when a row was written is a
            // board nobody reads twice.
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.DueOn == null)
            .ThenBy(item => item.DueOn)
            .ThenBy(item => item.Title)
            .Select(item => new
            {
                item.Id,
                item.Title,
                item.Status,
                item.Priority,
                item.ProjectId,
                item.AssigneeId,
                item.DueOn,
                item.EstimateMinutes,
                item.BlockedReason,
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
            row.Title,
            row.Status,
            row.Priority,
            row.ProjectId,
            row.ProjectId is { } project ? projects.GetValueOrDefault(project) : null,
            row.AssigneeId,
            row.AssigneeId is { } assignee ? people.GetValueOrDefault(assignee) : null,
            row.DueOn,
            row.EstimateMinutes,
            row.BlockedReason)).ToList();
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
    string Title,
    WorkItemStatus Status,
    Priority Priority,
    Guid? ProjectId,
    string? ProjectName,
    Guid? AssigneeId,
    string? AssigneeName,
    DateOnly? DueOn,
    int? EstimateMinutes,
    string? BlockedReason)
{
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
