using JiranisokoTech.Application.Time;
using JiranisokoTech.Domain.Time;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Time;

/// <summary>The firm's own dates, over the one context.</summary>
public sealed class OccasionRepository(AppDbContext database) : IOccasionRepository
{
    public Task<Occasion?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.Occasions.FirstOrDefaultAsync(one => one.Id == id, cancellationToken);

    /// <remarks>
    /// Anything still running counts as from that date, so a week-long office closure does not
    /// disappear off the maintenance screen on its second day.
    /// </remarks>
    public Task<List<Occasion>> FromAsync(
        DateOnly from, CancellationToken cancellationToken = default) =>
        database.Occasions
            .Where(one => (one.Until ?? one.On) >= from)
            .OrderBy(one => one.On)
            .ThenBy(one => one.Name)
            .ToListAsync(cancellationToken);

    public void Add(Occasion occasion) => database.Occasions.Add(occasion);

    public void Remove(Occasion occasion) => database.Occasions.Remove(occasion);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        database.SaveChangesAsync(cancellationToken);
}
