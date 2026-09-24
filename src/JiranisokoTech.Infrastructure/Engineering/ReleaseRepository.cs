using JiranisokoTech.Application.Engineering;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Engineering;

/// <summary>The reads and writes releases need.</summary>
public sealed class ReleaseRepository(AppDbContext database) : IReleaseRepository
{
    public Task<Release?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.Releases.FirstOrDefaultAsync(one => one.Id == id, cancellationToken);

    public Task<List<Release>> ForRepositoryAsync(
        Guid repositoryId, CancellationToken cancellationToken = default) =>
        database.Releases
            .AsNoTracking()
            .Where(one => one.RepositoryId == repositoryId)
            .ToListAsync(cancellationToken);

    public async Task<DateTimeOffset?> CommitAtAsync(
        Guid repositoryId, string sha, CancellationToken cancellationToken = default)
    {
        /*
         * Matched on the repository as well as the sha, although commits.Sha is unique on its
         * own. Not for the index — for the answer: a release of repository A must not find its
         * boundary in repository B because somebody pasted the wrong hash, and with the
         * repository in the predicate that mistake produces "not recorded here", which is a
         * sentence the page already knows how to say.
         */
        return await database.Commits
            .AsNoTracking()
            .Where(one => one.RepositoryId == repositoryId && one.Sha == sha)
            .Select(one => (DateTimeOffset?)one.At)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// The commits in the window, newest first, each with the work it belongs to.
    /// </summary>
    /// <remarks>
    /// Two queries and a stitch, rather than one query with a left join. The bound has to be
    /// applied to the commits before the work items are brought in — a join then a Take reads
    /// the join's rows, not three hundred commits — and a left join that survives a Take
    /// upstream is the kind of LINQ that translates today and stops translating on an EF
    /// upgrade. The second query is keyed on a few hundred ids at most.
    ///
    /// Left rather than inner, which is the part that matters: most commits belong to a work
    /// item and some do not — a hotfix pushed straight to main, a dependency bump, anything
    /// done before the branch naming convention was agreed. Dropping those would leave the
    /// changelog missing exactly the work nobody was tracking.
    ///
    /// The upper bound is inclusive and the lower bound is not, so the commit a release names is
    /// in its own changelog and the commit the previous release named is not in two.
    /// </remarks>
    public async Task<List<ChangelogEntry>> CommitsBetweenAsync(
        Guid repositoryId,
        DateTimeOffset? after,
        DateTimeOffset until,
        int most,
        CancellationToken cancellationToken = default)
    {
        var query = database.Commits
            .AsNoTracking()
            .Where(one => one.RepositoryId == repositoryId && one.At <= until);

        if (after is { } floor)
        {
            query = query.Where(one => one.At > floor);
        }

        var commits = await query
            .OrderByDescending(one => one.At)
            .Take(most)
            .Select(one => new
            {
                one.Sha,
                one.Message,
                one.Author,
                one.At,
                one.WorkItemId,
            })
            .ToListAsync(cancellationToken);

        var wanted = commits
            .Where(one => one.WorkItemId is not null)
            .Select(one => one.WorkItemId!.Value)
            .Distinct()
            .ToList();

        Dictionary<Guid, WorkLine> work;

        work = wanted.Count == 0
            ? []
            : await database.WorkItems
                .AsNoTracking()
                .Where(one => wanted.Contains(one.Id))
                .Select(one => new { one.Id, one.Number, one.Title })
                .ToDictionaryAsync(
                    one => one.Id,
                    one => new WorkLine(one.Number, one.Title),
                    cancellationToken);

        return
        [
            .. commits.Select(one =>
            {
                var item = one.WorkItemId is { } id ? work.GetValueOrDefault(id) : null;

                return new ChangelogEntry(
                    one.Sha,
                    one.Message,
                    one.Author,
                    one.At,
                    item?.Number,
                    item?.Title);
            })
        ];
    }

    public void Add(Release release) => database.Releases.Add(release);

    /// <summary>The two fields a changelog needs off a work item.</summary>
    private sealed record WorkLine(int Number, string Title);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        database.SaveChangesAsync(cancellationToken);
}
