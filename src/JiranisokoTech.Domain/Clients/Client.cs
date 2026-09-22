using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Clients;

public enum ClientStatus
{
    /// <summary>Talking to them. No work agreed.</summary>
    Prospect = 1,

    Active = 2,

    /// <summary>Still a client, nothing running.</summary>
    Dormant = 3,

    /// <summary>The relationship has ended.</summary>
    Former = 4,
}

/// <summary>
/// A firm that pays us.
/// </summary>
/// <remarks>
/// Kept deliberately thin. The temptation with a client record is to grow it
/// into a CRM — every conversation, every contact, every opportunity — and
/// three modules later nobody can find the invoice address. What lives here is
/// what the rest of this system needs: who they are, who to write to, and
/// whether they are still a client.
/// </remarks>
public sealed class Client : Entity, IAuditable
{
    private Client()
    {
        Name = string.Empty;
        Code = string.Empty;
    }

    private Client(string name, Slug code, string? contactName, string? contactEmail)
    {
        Name = Require(name, nameof(name));
        Code = code.Value;
        ContactName = Trim(contactName);
        ContactEmail = Trim(contactEmail)?.ToLowerInvariant();
        Status = ClientStatus.Prospect;

        Raise(new ClientTakenOn(Id, Name, Code));
    }

    public static Client TakeOn(
        string name, string? code = null, string? contactName = null, string? contactEmail = null) =>
        new(name, Slug.From(code ?? name), contactName, contactEmail);

    public string Name { get; private set; }

    /// <summary>The short name on invoices and in conversation. Fixed.</summary>
    public string Code { get; private init; }

    public string? ContactName { get; private set; }

    public string? ContactEmail { get; private set; }

    public string? Phone { get; private set; }

    /// <summary>Where invoices go, when that is not the contact address.</summary>
    public string? BillingEmail { get; private set; }

    public string? Address { get; private set; }

    public ClientStatus Status { get; private set; }

    /// <summary>
    /// How long they have to pay, in days.
    /// </summary>
    /// <remarks>
    /// On the client rather than typed per invoice, because it is a term of the
    /// relationship and re-entering it every month is how one invoice quietly
    /// ends up on ninety days.
    /// </remarks>
    public int PaymentTermDays { get; private set; } = 30;

    public bool IsCurrent => Status is ClientStatus.Prospect or ClientStatus.Active
        or ClientStatus.Dormant;

    /// <summary>Where invoices are actually sent.</summary>
    public string? InvoiceAddress => BillingEmail ?? ContactEmail;

    public void MoveTo(ClientStatus status)
    {
        if (Status == status)
        {
            return;
        }

        var from = Status;
        Status = status;

        Raise(new ClientStatusChanged(Id, Name, from, status));
    }

    public void Rename(string name) => Name = Require(name, nameof(name));

    public void ContactIs(string? name, string? email, string? phone)
    {
        ContactName = Trim(name);
        ContactEmail = Trim(email)?.ToLowerInvariant();
        Phone = Trim(phone);
    }

    public void BillTo(string? email, string? address)
    {
        BillingEmail = Trim(email)?.ToLowerInvariant();
        Address = Trim(address);
    }

    public void PaysWithin(int days)
    {
        if (days is < 0 or > 180)
        {
            throw new ArgumentOutOfRangeException(
                nameof(days), days, "Payment terms run from nothing to six months.");
        }

        PaymentTermDays = days;
    }

    /// <summary>Nothing here is a secret from anybody who can see clients.</summary>
    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Require(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();
}

public sealed record ClientTakenOn(Guid ClientId, string Name, string Code) : DomainEvent;

public sealed record ClientStatusChanged(
    Guid ClientId, string Name, ClientStatus From, ClientStatus To) : DomainEvent;
