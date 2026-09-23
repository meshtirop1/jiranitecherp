using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Audit;

/// <summary>
/// Reading the trail back.
/// </summary>
/// <remarks>
/// Until now this was write-only: every change was recorded in the same
/// transaction that made it, and nothing anywhere could show one. audit.view
/// was granted to four roles and did nothing. A trail nobody can read is a cost
/// with no benefit — it slows every write and answers no question.
///
/// Paged rather than filtered-and-hoped. This table grows with every change
/// anybody makes and is the largest in the system within a year; a page that
/// loads it all works perfectly for a month.
/// </remarks>
public sealed class AuditQueries(AppDbContext database)
{
    public const int PageSize = 50;

    public async Task<AuditPage> RecentAsync(
        string? subjectType = null,
        string? action = null,
        Guid? subjectId = null,
        int page = 0,
        CancellationToken cancellationToken = default)
    {
        var query = database.AuditEntries.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(subjectType))
        {
            query = query.Where(entry => entry.SubjectType == subjectType);
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            query = query.Where(entry => entry.Action == action);
        }

        if (subjectId is { } subject)
        {
            query = query.Where(entry => entry.SubjectId == subject);
        }

        var total = await query.CountAsync(cancellationToken);

        var entries = await query
            // Newest first: a trail is read to answer "what just happened",
            // and the day somebody reads it from the beginning they will use
            // the filters.
            .OrderByDescending(entry => entry.OccurredAt)
            .Skip(Math.Max(0, page) * PageSize)
            .Take(PageSize)
            .ToListAsync(cancellationToken);

        return new AuditPage(entries, total, Math.Max(0, page));
    }

    /// <summary>The kinds of thing that have been changed, for the filter.</summary>
    /// <remarks>
    /// Read from the trail rather than from the list of auditable types,
    /// because a type nothing has ever changed is a filter option that always
    /// returns nothing.
    /// </remarks>
    public Task<List<string>> SubjectTypesAsync(CancellationToken cancellationToken = default) =>
        database.AuditEntries
            .AsNoTracking()
            .Select(entry => entry.SubjectType)
            .Distinct()
            .OrderBy(name => name)
            .ToListAsync(cancellationToken);

    public Task<List<string>> ActionsAsync(CancellationToken cancellationToken = default) =>
        database.AuditEntries
            .AsNoTracking()
            .Select(entry => entry.Action)
            .Distinct()
            .OrderBy(name => name)
            .ToListAsync(cancellationToken);
}

public sealed record AuditPage(IReadOnlyList<AuditEntry> Entries, int Total, int Page)
{
    public bool HasMore => (Page + 1) * AuditQueries.PageSize < Total;

    public bool HasPrevious => Page > 0;
}
