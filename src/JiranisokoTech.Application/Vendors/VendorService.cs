using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Domain.Vendors;

namespace JiranisokoTech.Application.Vendors;

/// <summary>What the supplier book needs read and written.</summary>
public interface IVendorRepository
{
    Task<Vendor?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Vendor?> ByCodeAsync(string code, CancellationToken cancellationToken = default);

    Task<bool> CodeTakenAsync(
        string code, Guid? except = null, CancellationToken cancellationToken = default);

    Task<List<Vendor>> AllAsync(
        bool includeFormer = true, CancellationToken cancellationToken = default);

    Task<VendorContact?> FindContactAsync(Guid id, CancellationToken cancellationToken = default);

    Task<List<VendorContact>> ContactsForAsync(
        Guid vendorId, CancellationToken cancellationToken = default);

    void Add(Vendor vendor);

    void Add(VendorContact contact);

    Task SaveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The firm's suppliers, and who to ring at each of them.
/// </summary>
/// <remarks>
/// Section 62. The rules here are the ones needing more than one row: whether a short code is
/// already somebody else's, and which of a vendor's contacts is the one to call. The rest is on
/// the aggregate, where it can be tested without a database.
///
/// The one-main-contact policy is not written here. It lives in
/// <see cref="WhoToCallFirst"/> and is shared with the client contact book, because a rule that
/// exists in two services is a rule that will be changed in one of them.
/// </remarks>
public sealed class VendorService(IVendorRepository vendors, IClock clock)
{
    /// <summary>
    /// Put a supplier on the books.
    /// </summary>
    /// <remarks>
    /// The code is checked here as well as by the unique index, because the index's message is a
    /// constraint violation and this one can name the supplier that already holds it — and
    /// somebody typing a code that exists has usually found the row they were about to create.
    /// </remarks>
    public async Task<Vendor> TakeOnAsync(
        string name,
        string? code = null,
        string? supplies = null,
        CancellationToken cancellationToken = default)
    {
        var handle = Slug.From(code ?? name);

        if (await vendors.ByCodeAsync(handle.Value, cancellationToken) is { } already)
        {
            throw new InvalidOperationException(
                $"{already.Name} already uses the short name '{handle.Value}'. Give this one a "
                + "different one, or open the supplier that is already on file.");
        }

        var vendor = Vendor.TakeOn(name, handle.Value, supplies);

        vendors.Add(vendor);
        await vendors.SaveAsync(cancellationToken);

        return vendor;
    }

    public async Task DescribeAsync(
        Guid id,
        string name,
        string? supplies,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        var vendor = await Required(id, cancellationToken);

        vendor.Rename(name);
        vendor.Describe(supplies, notes);

        await vendors.SaveAsync(cancellationToken);
    }

    public async Task RegisteredAsync(
        Guid id,
        string? taxPin,
        string? address,
        int paymentTermDays,
        CancellationToken cancellationToken = default)
    {
        var vendor = await Required(id, cancellationToken);

        vendor.Registered(taxPin, address);
        vendor.PaidWithin(paymentTermDays);

        await vendors.SaveAsync(cancellationToken);
    }

    public async Task MoveToAsync(
        Guid id, VendorStatus status, CancellationToken cancellationToken = default)
    {
        var vendor = await Required(id, cancellationToken);

        vendor.MoveTo(status);

        await vendors.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Add somebody at that supplier.
    /// </summary>
    /// <remarks>
    /// The first person added becomes the one to call, without anybody being asked. A contact
    /// book whose only entry is marked as nothing in particular is a book where the next person
    /// to write to that company has to guess.
    /// </remarks>
    public async Task<VendorContact> AddContactAsync(
        Guid vendorId,
        string name,
        string? jobTitle = null,
        string? email = null,
        string? phone = null,
        CancellationToken cancellationToken = default)
    {
        await Required(vendorId, cancellationToken);

        var book = await vendors.ContactsForAsync(vendorId, cancellationToken);
        var first = book.All(one => !one.IsHere);

        var contact = VendorContact.At(
            vendorId, name, jobTitle, email, phone, first, clock.Now);

        vendors.Add(contact);
        await vendors.SaveAsync(cancellationToken);

        return contact;
    }

    public async Task UpdateContactAsync(
        Guid contactId,
        string name,
        string? jobTitle,
        string? email,
        string? phone,
        CancellationToken cancellationToken = default)
    {
        var contact = await RequiredContact(contactId, cancellationToken);

        contact.Update(name, jobTitle, email, phone);

        await vendors.SaveAsync(cancellationToken);
    }

    /// <summary>Make somebody the one to call, clearing whoever held it.</summary>
    public async Task PromoteAsync(Guid contactId, CancellationToken cancellationToken = default)
    {
        var contact = await RequiredContact(contactId, cancellationToken);
        var book = await vendors.ContactsForAsync(contact.VendorId, cancellationToken);

        WhoToCallFirst.Promote(book, contactId);

        await vendors.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Record that somebody has left that company.
    /// </summary>
    /// <remarks>
    /// Kept rather than deleted, and the post is handed on rather than left vacant — both through
    /// the shared rule, so a client contact leaving and a vendor contact leaving behave the same
    /// way for the same reason.
    /// </remarks>
    public async Task GoneAsync(Guid contactId, CancellationToken cancellationToken = default)
    {
        var contact = await RequiredContact(contactId, cancellationToken);
        var wasTheOneToCall = contact.IsMain;

        contact.Gone(clock.Now);

        if (wasTheOneToCall)
        {
            var book = await vendors.ContactsForAsync(contact.VendorId, cancellationToken);

            WhoToCallFirst.Inherit(book, contact.Id);
        }

        await vendors.SaveAsync(cancellationToken);
    }

    public Task<List<Vendor>> AllAsync(
        bool includeFormer = true, CancellationToken cancellationToken = default) =>
        vendors.AllAsync(includeFormer, cancellationToken);

    public Task<Vendor?> OneAsync(Guid id, CancellationToken cancellationToken = default) =>
        vendors.FindAsync(id, cancellationToken);

    public Task<List<VendorContact>> ContactsForAsync(
        Guid vendorId, CancellationToken cancellationToken = default) =>
        vendors.ContactsForAsync(vendorId, cancellationToken);

    private async Task<Vendor> Required(Guid id, CancellationToken cancellationToken) =>
        await vendors.FindAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("That supplier is not on file.");

    private async Task<VendorContact> RequiredContact(
        Guid id, CancellationToken cancellationToken) =>
        await vendors.FindContactAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("There is no contact with that identifier.");
}
