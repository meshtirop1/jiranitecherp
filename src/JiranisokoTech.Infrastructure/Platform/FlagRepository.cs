using JiranisokoTech.Application.Platform;
using JiranisokoTech.Domain.Platform;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Platform;

/// <summary>The reads and writes feature flags need.</summary>
public sealed class FlagRepository(AppDbContext database) : IFlagRepository
{
    /// <remarks>
    /// The settings and the history come with it, always — every write touches one and appends
    /// to the other, and an owned collection EF never loaded is one it happily replaces with
    /// nothing.
    /// </remarks>
    public Task<Flag?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.Flags
            .Include(one => one.Settings)
            .Include(one => one.Changes)
            .FirstOrDefaultAsync(one => one.Id == id, cancellationToken);

    public Task<Flag?> ByKeyAsync(string key, CancellationToken cancellationToken = default) =>
        database.Flags
            .Include(one => one.Settings)
            .FirstOrDefaultAsync(one => one.Key == key, cancellationToken);

    /// <remarks>
    /// The settings come with it because the list shows every environment's state, and the
    /// API's answer is built from exactly this. The history does not: a page listing forty
    /// flags does not want four hundred changes, and the one flag somebody opens loads its own.
    /// </remarks>
    public Task<List<Flag>> AllAsync(CancellationToken cancellationToken = default) =>
        database.Flags
            .AsNoTracking()
            .Include(one => one.Settings)
            .OrderBy(one => one.RetiredAt != null)
            .ThenBy(one => one.Key)
            .ToListAsync(cancellationToken);

    public async Task<List<FlagMovement>> MovedBetweenAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        /*
         * Over the owned collection directly rather than by loading flags and filtering in
         * memory, so that an incident's panel reads the changes in the window and not every
         * change the firm has ever made.
         */
        var moved = await database.Flags
            .AsNoTracking()
            .SelectMany(
                flag => flag.Changes,
                (flag, change) => new
                {
                    flag.Id,
                    flag.Key,
                    change.Environment,
                    change.On,
                    change.Why,
                    change.ById,
                    change.At,
                })
            .Where(one => one.At >= from && one.At <= to)
            .OrderByDescending(one => one.At)
            .ToListAsync(cancellationToken);

        return
        [
            .. moved.Select(one => new FlagMovement(
                one.Id, one.Key, one.Environment, one.On, one.Why, one.ById, one.At))
        ];
    }

    public void Add(Flag flag) => database.Flags.Add(flag);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        database.SaveChangesAsync(cancellationToken);
}
