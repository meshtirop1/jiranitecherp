using JiranisokoTech.Application.Knowledge;
using JiranisokoTech.Domain.Knowledge;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Knowledge;

public sealed class KnowledgeRepository(AppDbContext database) : IKnowledgeRepository
{
    public Task<Article?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.Articles.FirstOrDefaultAsync(one => one.Id == id, cancellationToken);

    /// <summary>
    /// The article at an address.
    /// </summary>
    /// <remarks>
    /// Matched exactly rather than without regard to case, because the key is a slug and a slug
    /// is lower case by construction. A case-insensitive read against a case-sensitive unique
    /// index means the lookup and the constraint disagree about whether two rows are one row,
    /// which this codebase has met once already on a vendor's code.
    /// </remarks>
    public Task<Article?> ByKeyAsync(
        string key, CancellationToken cancellationToken = default) =>
        database.Articles.FirstOrDefaultAsync(one => one.Key == key, cancellationToken);

    public Task<List<string>> KeysLikeAsync(
        string key, CancellationToken cancellationToken = default) =>
        database.Articles
            .Where(one => one.Key.StartsWith(key))
            .Select(one => one.Key)
            .ToListAsync(cancellationToken);

    public void Add(Article article) => database.Articles.Add(article);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        database.SaveChangesAsync(cancellationToken);
}

/// <summary>One line of the knowledge base's own list.</summary>
public sealed record ArticleRow(
    Guid Id,
    string Key,
    string Title,
    string Summary,
    string? Labels,
    ArticleState State,
    string Owner,
    DateOnly? ReviewBy,
    bool IsStale);

/// <summary>
/// Reading the knowledge base.
/// </summary>
/// <remarks>
/// Section 25. Every read here is a projection: the list shows forty summaries and must never
/// load forty bodies of forty thousand characters to do it, which is the difference between a
/// page that opens and one people stop using.
/// </remarks>
public sealed class KnowledgeQueries(AppDbContext database)
{
    /// <summary>
    /// Enough to fill a page, and a bound because there is no paging here.
    /// </summary>
    /// <remarks>
    /// A knowledge base worth having outgrows a screen, and the answer to that is the search
    /// box rather than a pager — somebody looking for an article knows a word in its title.
    /// What the cap prevents is the page that quietly takes two seconds longer every month
    /// until somebody notices, which is the shape the invoices screen was found in.
    /// </remarks>
    public const int Most = 200;

    public async Task<List<ArticleRow>> ListAsync(
        DateOnly today,
        string? term = null,
        string? label = null,
        bool includeUnpublished = false,
        bool onlyStale = false,
        CancellationToken cancellationToken = default)
    {
        var query = database.Articles.AsNoTracking();

        /*
         * Drafts and retired articles are hidden unless asked for, and the asking is behind the
         * write permission on the page. A draft is somebody's half-written thought and a
         * retired article describes something the firm has stopped doing; both on the ordinary
         * list would mean the knowledge base answers questions with work in progress.
         */
        if (!includeUnpublished)
        {
            query = query.Where(one => one.State == ArticleState.Published);
        }

        if (onlyStale)
        {
            query = query.Where(one =>
                one.State == ArticleState.Published
                && one.ReviewBy != null
                && one.ReviewBy < today);
        }

        if (term is { Length: > 0 })
        {
            /*
             * Title and summary, not the body. Matching the body would turn a search for
             * "deploy" into every article that mentions it in passing, and the summary exists
             * precisely so there is a short piece of text worth matching against.
             *
             * ToLower().Contains spelled out so PostgreSQL and SQLite produce the same answer:
             * Postgres LIKE is case-sensitive and SQLite's is not, which is the difference
             * found by somebody typing their search in lower case and getting nothing.
             */
            var wanted = term.Trim().ToLowerInvariant();

            query = query.Where(one =>
                one.Title.ToLower().Contains(wanted) || one.Summary.ToLower().Contains(wanted));
        }

        if (label is { Length: > 0 })
        {
            var wanted = label.Trim().ToLowerInvariant();

            query = query.Where(one => one.Labels != null && one.Labels.Contains(wanted));
        }

        var rows = await query
            .OrderBy(one => one.State)
            .ThenBy(one => one.Title)
            .Take(Most)
            .Select(one => new
            {
                one.Id,
                one.Key,
                one.Title,
                one.Summary,
                one.Labels,
                one.State,
                one.ReviewBy,
                Owner = database.Employees
                    .Where(person => person.Id == one.OwnerId)
                    .Select(person => person.FullName)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        return
        [
            .. rows.Select(row => new ArticleRow(
                row.Id,
                row.Key,
                row.Title,
                row.Summary,
                row.Labels,
                row.State,
                row.Owner ?? "nobody",
                row.ReviewBy,
                row.State == ArticleState.Published
                    && row.ReviewBy is { } due
                    && due < today)),
        ];
    }

    /// <summary>
    /// Every label anybody has used, with how many articles carry it.
    /// </summary>
    /// <remarks>
    /// Split here rather than in SQL because the labels are one comma-separated column, which
    /// is the shape a document's tags already take. It reads only that column of published
    /// articles, so the cost is one narrow scan rather than the table.
    ///
    /// A label table with a join would be the tidier schema and it is not worth it: labels are
    /// typed freely, a firm of thirty produces a few dozen, and the tidier schema buys a rename
    /// feature nobody has asked for at the price of a screen for managing labels.
    /// </remarks>
    public async Task<IReadOnlyList<(string Label, int Count)>> LabelsAsync(
        CancellationToken cancellationToken = default)
    {
        var written = await database.Articles
            .AsNoTracking()
            .Where(one => one.State == ArticleState.Published && one.Labels != null)
            .Select(one => one.Labels!)
            .ToListAsync(cancellationToken);

        return
        [
            .. written
                .SelectMany(labels => labels.Split(
                    ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .GroupBy(one => one, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => (group.Key, group.Count())),
        ];
    }

    /// <summary>How many published articles are past their review date.</summary>
    /// <remarks>
    /// One number, for the heading. It is the number this whole section exists to keep at zero,
    /// so it belongs where somebody sees it without asking for it.
    /// </remarks>
    public Task<int> StaleCountAsync(
        DateOnly today, CancellationToken cancellationToken = default) =>
        database.Articles.CountAsync(
            one => one.State == ArticleState.Published
                && one.ReviewBy != null
                && one.ReviewBy < today,
            cancellationToken);
}
