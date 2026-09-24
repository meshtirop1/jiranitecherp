using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Vendors;

/// <summary>Where the firm's relationship with a supplier stands.</summary>
public enum VendorStatus
{
    /// <summary>Being bought from.</summary>
    Active = 1,

    /// <summary>Still a supplier, nothing running, or a dispute being settled.</summary>
    OnHold = 2,

    /// <summary>No longer bought from. Kept, because what was bought stays bought.</summary>
    Former = 3,
}

/// <summary>
/// A company the firm buys from.
/// </summary>
/// <remarks>
/// Section 62, and it closes a gap section 17 named out loud. <see cref="Contracts.Agreement"/>
/// says a vendor agreement is "with a company this system does not otherwise know about, and
/// inventing a supplier table to hold a name would be section 62's work done badly in passing".
/// That was true, and it was true in three places rather than one: a supplier's name was typed by
/// hand onto an agreement, onto a standing cost, and onto a platform resource. Three free-text
/// columns meant the firm could hold a signed hosting agreement, a monthly charge and a running
/// server all naming the same company, and nothing could join them up.
///
/// <b>Its own aggregate, built to <see cref="Clients.Client"/>'s pattern but sharing no table.</b>
/// The tempting design is one Party table with IsClient and IsVendor flags, and it fails on the
/// ordinary case: Safaricom sells this firm airtime and could perfectly well buy software from it.
/// One row means one status column, and the day that relationship ends on one side it would have
/// to say Former while the other side is still Active. Two rows cost a little duplication and can
/// each tell the truth.
///
/// <b>No bank details.</b> Not an omission — nothing in this system pays anybody, so no code would
/// ever read an account number, and there is no column-level encryption here to hold one safely.
/// A field nobody reads and nothing protects is a liability with a label on it.
/// </remarks>
public sealed class Vendor : Entity, IAuditable
{
    private Vendor()
    {
        Name = string.Empty;
        Code = string.Empty;
    }

    private Vendor(string name, Slug code, string? supplies)
    {
        Name = Require(name, nameof(name));
        Code = code.Value;
        Supplies = Trim(supplies);
        Status = VendorStatus.Active;

        Raise(new VendorTakenOn(Id, Name, Code));
    }

    public static Vendor TakeOn(string name, string? code = null, string? supplies = null) =>
        new(name, Slug.From(code ?? name), supplies);

    public string Name { get; private set; }

    /// <summary>
    /// The short name in addresses and in conversation. Fixed once the vendor exists.
    /// </summary>
    /// <remarks>
    /// This carries the uniqueness rather than the name, and the reason is the one
    /// <see cref="Assets.Asset"/> learned by upper-casing its tag: a unique index on a name is
    /// case-sensitive, so "Safaricom" and "safaricom" would be two suppliers, and the section
    /// fails the moment there are two Safaricom rows. A slug is reduced on the way in, so the
    /// constraint catches what it looks like it catches.
    /// </remarks>
    public string Code { get; private init; }

    public Slug Handle => Common.Slug.FromStored(Code);

    /// <summary>What the firm buys from them, in a few words.</summary>
    public string? Supplies { get; private set; }

    /*
     * No ContactName, ContactEmail or Phone on the vendor row, unlike the client row.
     *
     * Those exist on a client because contacts arrived later — Contact's own remarks say "a
     * client already carried one contact name and one address" — and copying a transitional
     * state into a brand-new table would be copying a scar. A supplier's people live in
     * vendor_contacts from the first migration, where the one-to-ring rule can reach them.
     */

    public string? Address { get; private set; }

    /// <summary>
    /// Their KRA PIN.
    /// </summary>
    /// <remarks>
    /// Upper-cased on the way in, because a case-sensitive comparison would make P051234567X and
    /// p051234567x two taxpayers — the same fault the asset tag avoids, arrived at from the same
    /// direction. Optional: a casual supplier may not have been asked for one, and refusing to
    /// record the supplier until somebody finds it is how the record stops being kept.
    /// </remarks>
    public string? TaxPin { get; private set; }

    public VendorStatus Status { get; private set; }

    /// <summary>
    /// How long the firm has to pay them, in days.
    /// </summary>
    /// <remarks>
    /// The mirror of <see cref="Clients.Client.PaymentTermDays"/>, and on the vendor for the same
    /// reason it is on the client: it is a term of the relationship rather than something to
    /// re-type on every order, and re-entering it is how one bill quietly ends up on ninety days.
    /// </remarks>
    public int PaymentTermDays { get; private set; } = 30;

    public string? Notes { get; private set; }

    /// <summary>Still bought from, or could be.</summary>
    public bool IsCurrent => Status is VendorStatus.Active or VendorStatus.OnHold;

    public void MoveTo(VendorStatus status)
    {
        if (Status == status)
        {
            return;
        }

        var from = Status;
        Status = status;

        Raise(new VendorStatusChanged(Id, Name, from, status));
    }

    public void Rename(string name) => Name = Require(name, nameof(name));

    public void Describe(string? supplies, string? notes)
    {
        Supplies = Trim(supplies);
        Notes = Trim(notes);
    }

    public void Registered(string? taxPin, string? address)
    {
        TaxPin = Trim(taxPin)?.ToUpperInvariant();
        Address = Trim(address);
    }

    public void PaidWithin(int days)
    {
        if (days is < 0 or > 180)
        {
            throw new ArgumentOutOfRangeException(
                nameof(days), days, "Payment terms run from nothing to six months.");
        }

        PaymentTermDays = days;
    }

    /// <summary>
    /// Nothing on the supplier row is a secret from anybody who can see suppliers.
    /// </summary>
    /// <remarks>
    /// The people are the sensitive part, and they are not on this row — a contact's direct line
    /// is excluded on <see cref="VendorContact"/>, where it lives.
    /// </remarks>
    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Require(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();
}

public sealed record VendorTakenOn(Guid VendorId, string Name, string Code) : DomainEvent;

public sealed record VendorStatusChanged(
    Guid VendorId, string Name, VendorStatus From, VendorStatus To) : DomainEvent;
