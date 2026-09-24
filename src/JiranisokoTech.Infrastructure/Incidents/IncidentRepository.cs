using JiranisokoTech.Application.Incidents;
using JiranisokoTech.Domain.Incidents;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Incidents;

/// <summary>The reads and writes an incident needs.</summary>
public sealed class IncidentRepository(AppDbContext database) : IIncidentRepository
{
    /// <remarks>
    /// The timeline comes with it, always. Every write to an incident adds a line, so a load
    /// without the notes would be a load that cannot be saved correctly — and an owned
    /// collection that was never loaded is one EF happily replaces with nothing.
    /// </remarks>
    public Task<Incident?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.Incidents
            .Include(one => one.Notes)
            .FirstOrDefaultAsync(one => one.Id == id, cancellationToken);

    public Task<Incident?> ByNumberAsync(
        int number, CancellationToken cancellationToken = default) =>
        database.Incidents
            .Include(one => one.Notes)
            .FirstOrDefaultAsync(one => one.Number == number, cancellationToken);

    public async Task<int> LastNumberAsync(CancellationToken cancellationToken = default) =>
        await database.Incidents
            .AsNoTracking()
            .Select(one => (int?)one.Number)
            .MaxAsync(cancellationToken) ?? 0;

    public Task<Postmortem?> ReviewForAsync(
        Guid incidentId, CancellationToken cancellationToken = default) =>
        database.Postmortems
            .Include(one => one.Actions)
            .FirstOrDefaultAsync(one => one.IncidentId == incidentId, cancellationToken);

    public void Add(Incident incident) => database.Incidents.Add(incident);

    public void Add(Postmortem review) => database.Postmortems.Add(review);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        database.SaveChangesAsync(cancellationToken);
}
