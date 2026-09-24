using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Vendors;

/// <summary>
/// A person at a vendor.
/// </summary>
/// <remarks>
/// The same record as <see cref="Clients.Contact"/>, and deliberately a separate entity in a
/// separate table rather than that one made polymorphic. <c>Contact.ClientId</c> is non-nullable
/// with a <c>private init</c> and a cascading foreign key; making the owner nullable and doubled
/// would let a contact belong to nothing, or to both, and would replace a guarantee the database
/// makes with a rule a service has to remember. It would also cost the cascade, so removing a
/// client would orphan its contacts rather than take them with it.
///
/// The shape is repeated. The <i>rule</i> is not — "exactly one person is the one to call first"
/// lives once, in <see cref="WhoToCallFirst"/>, and both contact books use it. A policy written
/// twice is a policy that will be changed once.
/// </remarks>
public sealed class VendorContact : Entity, IAuditable, IContactInABook
{
    private VendorContact() => Name = string.Empty;

    private VendorContact(
        Guid vendorId,
        string name,
        string? jobTitle,
        string? email,
        string? phone,
        bool isMain,
        DateTimeOffset at)
    {
        VendorId = vendorId;
        Name = Required(name, nameof(name));
        JobTitle = Trimmed(jobTitle);
        Email = Trimmed(email)?.ToLowerInvariant();
        Phone = Trimmed(phone);
        IsMain = isMain;
        AddedAt = at;
    }

    public static VendorContact At(
        Guid vendorId,
        string name,
        string? jobTitle = null,
        string? email = null,
        string? phone = null,
        bool isMain = false,
        DateTimeOffset at = default) =>
        new(vendorId, name, jobTitle, email, phone, isMain, at);

    public Guid VendorId { get; private init; }

    public string Name { get; private set; }

    public string? JobTitle { get; private set; }

    public string? Email { get; private set; }

    public string? Phone { get; private set; }

    /// <summary>The one to ring first.</summary>
    public bool IsMain { get; private set; }

    public DateTimeOffset AddedAt { get; private init; }

    /// <summary>
    /// When they left that company. Null while they are still there.
    /// </summary>
    /// <remarks>
    /// Kept rather than deleted, for the reason a client contact is: an email in the archive from
    /// somebody nobody can place is worse than a row saying they left in March.
    /// </remarks>
    public DateTimeOffset? GoneAt { get; private set; }

    public bool IsHere => GoneAt is null;

    public void Update(string name, string? jobTitle, string? email, string? phone)
    {
        Name = Required(name, nameof(name));
        JobTitle = Trimmed(jobTitle);
        Email = Trimmed(email)?.ToLowerInvariant();
        Phone = Trimmed(phone);
    }

    public void Main(bool isMain) => IsMain = isMain;

    public void Gone(DateTimeOffset at)
    {
        if (!IsHere)
        {
            return;
        }

        GoneAt = at;

        // Somebody who has left cannot be the first person to call, and leaving that flag set is
        // how an order goes to an address that bounces for a year.
        IsMain = false;
    }

    /// <summary>
    /// A direct line belongs to a person outside this firm.
    /// </summary>
    /// <remarks>
    /// The same exclusion a client contact's number gets, for the same reason: nobody
    /// administering this system needs a stranger's mobile number in a table that is never
    /// pruned.
    /// </remarks>
    public static IReadOnlySet<string> AuditExcludes { get; } =
        new HashSet<string> { nameof(Phone) };

    private static string Required(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
