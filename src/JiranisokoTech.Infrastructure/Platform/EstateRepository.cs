using JiranisokoTech.Application.Platform;
using JiranisokoTech.Domain.Platform;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Platform;

/// <summary>The reads and writes the catalogue and the register need.</summary>
public sealed class EstateRepository(AppDbContext database) : IEstateRepository
{
    public Task<Service?> FindServiceAsync(
        Guid id, CancellationToken cancellationToken = default) =>
        database.Services.FirstOrDefaultAsync(one => one.Id == id, cancellationToken);

    /// <remarks>
    /// Live first, then the retired ones, and by how much each matters within that. A catalogue
    /// is read in an emergency, and the order it comes back in is the order somebody reads it.
    /// </remarks>
    public Task<List<Service>> ServicesAsync(CancellationToken cancellationToken = default) =>
        database.Services
            .AsNoTracking()
            .OrderBy(one => one.RetiredAt != null)
            .ThenBy(one => one.Matters)
            .ThenBy(one => one.Name)
            .ToListAsync(cancellationToken);

    /// <remarks>
    /// Case-insensitively, because "Despatch board" and "despatch board" are the same service to
    /// everybody except a string comparison — and the whole point of the check is that a second
    /// entry splits an incident history in half.
    /// </remarks>
    public Task<bool> ServiceNamedAsync(
        string name, Guid? except = null, CancellationToken cancellationToken = default) =>
        database.Services.AnyAsync(
            one => one.Name.ToLower() == name.Trim().ToLower() && one.Id != except,
            cancellationToken);

    public Task<Resource?> FindResourceAsync(
        Guid id, CancellationToken cancellationToken = default) =>
        database.Resources.FirstOrDefaultAsync(one => one.Id == id, cancellationToken);

    public Task<List<Resource>> ResourcesAsync(CancellationToken cancellationToken = default) =>
        database.Resources
            .AsNoTracking()
            .OrderBy(one => one.RetiredAt != null)
            .ThenBy(one => one.Environment)
            .ThenBy(one => one.Kind)
            .ThenBy(one => one.Name)
            .ToListAsync(cancellationToken);

    public Task<List<Resource>> ResourcesForAsync(
        Guid serviceId, CancellationToken cancellationToken = default) =>
        database.Resources
            .AsNoTracking()
            .Where(one => one.ServiceId == serviceId && one.RetiredAt == null)
            .OrderBy(one => one.Environment)
            .ThenBy(one => one.Name)
            .ToListAsync(cancellationToken);

    public void Add(Service service) => database.Services.Add(service);

    public void Add(Resource resource) => database.Resources.Add(resource);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        database.SaveChangesAsync(cancellationToken);
}
