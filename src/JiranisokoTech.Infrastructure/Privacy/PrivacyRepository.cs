using JiranisokoTech.Application.Privacy;
using JiranisokoTech.Domain.Privacy;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Privacy;

/// <summary>The privacy register, over the one context.</summary>
public sealed class PrivacyRepository(AppDbContext database) : IPrivacyRepository
{
    public Task<PrivacyRequest?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.PrivacyRequests
            .Include(one => one.Outcomes)
            .FirstOrDefaultAsync(one => one.Id == id, cancellationToken);

    /// <remarks>
    /// Unanswered first and by deadline within that, because the only urgent thing on this
    /// register is a statutory clock — thirty days from when the request was made, whether or not
    /// anybody has looked at it.
    /// </remarks>
    public Task<List<PrivacyRequest>> AllAsync(CancellationToken cancellationToken = default) =>
        database.PrivacyRequests
            .AsNoTracking()
            .Include(one => one.Outcomes)
            .OrderBy(one => one.AnsweredAt != null)
            .ThenBy(one => one.ReceivedOn)
            .ToListAsync(cancellationToken);

    public async Task<int> LastNumberAsync(
        int year, CancellationToken cancellationToken = default)
    {
        var prefix = $"DPA-{year}-";

        var last = await database.PrivacyRequests
            .AsNoTracking()
            .Where(one => one.Reference.StartsWith(prefix))
            .OrderByDescending(one => one.Reference)
            .Select(one => one.Reference)
            .FirstOrDefaultAsync(cancellationToken);

        return last is not null && int.TryParse(last[prefix.Length..], out var number)
            ? number
            : 0;
    }

    public void Add(PrivacyRequest request) => database.PrivacyRequests.Add(request);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        database.SaveChangesAsync(cancellationToken);
}
