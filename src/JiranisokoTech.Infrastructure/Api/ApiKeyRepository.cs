using JiranisokoTech.Application.Api;
using JiranisokoTech.Domain.Api;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Api;

public sealed class ApiKeyRepository(AppDbContext database) : IApiKeyRepository
{
    public Task<ApiKey?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.ApiKeys.FirstOrDefaultAsync(key => key.Id == id, cancellationToken);

    public Task<ApiKey?> ByHashAsync(string hash, CancellationToken cancellationToken = default) =>
        database.ApiKeys.FirstOrDefaultAsync(key => key.Hash == hash, cancellationToken);

    public Task<List<ApiKey>> AllAsync(CancellationToken cancellationToken = default) =>
        database.ApiKeys
            .AsNoTracking()
            .OrderByDescending(key => key.CreatedAt)
            .ToListAsync(cancellationToken);

    public void Add(ApiKey key) => database.ApiKeys.Add(key);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        database.SaveChangesAsync(cancellationToken);
}

