using JiranisokoTech.Application.Assets;
using JiranisokoTech.Domain.Assets;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Assets;

/// <summary>The reads and writes the asset register needs.</summary>
public sealed class AssetRepository(AppDbContext database) : IAssetRepository
{
    /// <remarks>
    /// The movements come with it, always. Every write to an asset appends one, and an owned
    /// collection EF never loaded is one it happily replaces with nothing.
    /// </remarks>
    public Task<Asset?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.Assets
            .Include(one => one.Movements)
            .FirstOrDefaultAsync(one => one.Id == id, cancellationToken);

    public Task<Asset?> ByTagAsync(string tag, CancellationToken cancellationToken = default) =>
        database.Assets
            .Include(one => one.Movements)
            .FirstOrDefaultAsync(one => one.Tag == tag.Trim().ToUpper(), cancellationToken);

    public Task<bool> TagTakenAsync(
        string tag, Guid? except = null, CancellationToken cancellationToken = default) =>
        database.Assets.AnyAsync(
            one => one.Tag == tag.Trim().ToUpper() && one.Id != except, cancellationToken);

    /// <remarks>
    /// Live things first and then the rest, because a register is read to find something the
    /// firm still has. Retired and lost rows stay in the list — they are the answer to "what
    /// happened to JD-014" — but they are not what anybody is scrolling for.
    /// </remarks>
    public Task<List<Asset>> AllAsync(CancellationToken cancellationToken = default) =>
        database.Assets
            .AsNoTracking()
            .OrderBy(one => one.Status == AssetStatus.Retired || one.Status == AssetStatus.Lost)
            .ThenBy(one => one.Tag)
            .ToListAsync(cancellationToken);

    public Task<List<Asset>> HeldByAsync(
        Guid personId, CancellationToken cancellationToken = default) =>
        database.Assets
            .AsNoTracking()
            .Where(one => one.HeldById == personId && one.Status == AssetStatus.Issued)
            .OrderBy(one => one.Tag)
            .ToListAsync(cancellationToken);

    public Task<List<Asset>> InStockAsync(CancellationToken cancellationToken = default) =>
        database.Assets
            .AsNoTracking()
            .Where(one => one.Status == AssetStatus.InStock)
            .OrderBy(one => one.BoughtOn)
            .ThenBy(one => one.Tag)
            .ToListAsync(cancellationToken);

    public void Add(Asset asset) => database.Assets.Add(asset);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        database.SaveChangesAsync(cancellationToken);
}
