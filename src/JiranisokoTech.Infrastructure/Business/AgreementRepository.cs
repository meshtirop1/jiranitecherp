using JiranisokoTech.Application.Business;
using JiranisokoTech.Domain.Contracts;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Business;

/// <summary>The reads and writes the firm's own paperwork needs.</summary>
public sealed class AgreementRepository(AppDbContext database) : IAgreementRepository
{
    public Task<Agreement?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.Agreements.FirstOrDefaultAsync(one => one.Id == id, cancellationToken);

    /// <remarks>
    /// Case-insensitively, because a reference is typed by a person and JTS-NDA-2026-004 and
    /// jts-nda-2026-004 are the same piece of paper to everybody except a string comparison.
    /// The unique index underneath is case-sensitive and catches the exact repeat; this catches
    /// the one that actually happens, and can say so in a sentence.
    /// </remarks>
    public Task<bool> ReferenceTakenAsync(
        string reference, Guid? except = null, CancellationToken cancellationToken = default) =>
        database.Agreements.AnyAsync(
            one => one.Reference.ToLower() == reference.Trim().ToLower() && one.Id != except,
            cancellationToken);

    /// <remarks>
    /// Live first, then what is over, and by what runs out soonest within that — which is the
    /// order somebody reads this list in, because the only urgent thing on it is a date.
    /// </remarks>
    public Task<List<Agreement>> AllAsync(CancellationToken cancellationToken = default) =>
        database.Agreements
            .AsNoTracking()
            .OrderBy(one => one.State == AgreementState.Superseded
                || one.State == AgreementState.Ended)
            .ThenBy(one => one.EndsOn == null)
            .ThenBy(one => one.EndsOn)
            .ThenBy(one => one.Reference)
            .ToListAsync(cancellationToken);

    public Task<List<Agreement>> ForEmployeeAsync(
        Guid employeeId, CancellationToken cancellationToken = default) =>
        database.Agreements
            .AsNoTracking()
            .Where(one => one.EmployeeId == employeeId)
            .OrderByDescending(one => one.DraftedAt)
            .ToListAsync(cancellationToken);

    public void Add(Agreement agreement) => database.Agreements.Add(agreement);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        database.SaveChangesAsync(cancellationToken);
}
