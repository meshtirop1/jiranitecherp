using JiranisokoTech.Application.Vendors;
using JiranisokoTech.Domain.Vendors;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Vendors;

/// <summary>The supplier reads and writes, over the one context.</summary>
public sealed class VendorRepository(AppDbContext database) : IVendorRepository
{
    public Task<Vendor?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.Vendors.FirstOrDefaultAsync(one => one.Id == id, cancellationToken);

    public Task<Vendor?> ByCodeAsync(
        string code, CancellationToken cancellationToken = default) =>
        database.Vendors.FirstOrDefaultAsync(one => one.Code == code, cancellationToken);

    public Task<bool> CodeTakenAsync(
        string code, Guid? except = null, CancellationToken cancellationToken = default) =>
        database.Vendors.AnyAsync(one => one.Code == code && one.Id != except, cancellationToken);

    /// <remarks>
    /// Current suppliers first, then by name. A list of suppliers is read to find one, and the
    /// ones somebody is looking for are nearly always the ones still being bought from.
    /// </remarks>
    public async Task<List<Vendor>> AllAsync(
        bool includeFormer = true, CancellationToken cancellationToken = default)
    {
        var query = database.Vendors.AsNoTracking();

        if (!includeFormer)
        {
            query = query.Where(one => one.Status != VendorStatus.Former);
        }

        return await query
            .OrderBy(one => one.Status == VendorStatus.Former)
            .ThenBy(one => one.Name)
            .ToListAsync(cancellationToken);
    }

    public Task<VendorContact?> FindContactAsync(
        Guid id, CancellationToken cancellationToken = default) =>
        database.VendorContacts.FirstOrDefaultAsync(one => one.Id == id, cancellationToken);

    /// <remarks>
    /// Everybody, including those who have left, and the one to call first at the top. The
    /// leavers are what explain a year-old email from somebody nobody can place, which is the
    /// reason the rows are kept rather than deleted.
    /// </remarks>
    public Task<List<VendorContact>> ContactsForAsync(
        Guid vendorId, CancellationToken cancellationToken = default) =>
        database.VendorContacts
            .Where(one => one.VendorId == vendorId)
            .OrderByDescending(one => one.IsMain)
            .ThenBy(one => one.GoneAt != null)
            .ThenBy(one => one.AddedAt)
            .ToListAsync(cancellationToken);

    public void Add(Vendor vendor) => database.Vendors.Add(vendor);

    public void Add(VendorContact contact) => database.VendorContacts.Add(contact);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        database.SaveChangesAsync(cancellationToken);
}
