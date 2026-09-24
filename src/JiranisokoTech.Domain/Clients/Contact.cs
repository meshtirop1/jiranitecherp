using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Clients;

/// <summary>
/// A person at a client.
/// </summary>
/// <remarks>
/// A client already carried one contact name and one address, which is enough until the
/// first time somebody has to ask who signs and who pays — and those are rarely the same
/// person. An invoice sent to the person who asked for the work is an invoice that sits in
/// their inbox for six weeks.
///
/// <b>Its own record rather than more fields on the client.</b> People leave, and a contact
/// who has left is a fact worth keeping: the emails sent to them, the meetings they were in,
/// and the reason nobody there is answering. Overwriting a name loses all of it.
///
/// <b>One contact is the main one.</b> Not a role enum with twelve values nobody maintains:
/// what a person actually needs to know is who to call first, and everything beyond that is
/// in the job title they typed.
/// </remarks>
public sealed class Contact : Entity, IAuditable, IContactInABook
{
    private Contact()
    {
        Name = string.Empty;
    }

    private Contact(
        Guid clientId,
        string name,
        string? jobTitle,
        string? email,
        string? phone,
        bool isMain,
        DateTimeOffset at)
    {
        ClientId = clientId;
        Name = Required(name, nameof(name));
        JobTitle = Trimmed(jobTitle);
        Email = Trimmed(email)?.ToLowerInvariant();
        Phone = Trimmed(phone);
        IsMain = isMain;
        AddedAt = at;
    }

    public static Contact At(
        Guid clientId,
        string name,
        string? jobTitle = null,
        string? email = null,
        string? phone = null,
        bool isMain = false,
        DateTimeOffset at = default) =>
        new(clientId, name, jobTitle, email, phone, isMain, at);

    public Guid ClientId { get; private init; }

    public string Name { get; private set; }

    /// <summary>What they do there, in their own words.</summary>
    public string? JobTitle { get; private set; }

    public string? Email { get; private set; }

    public string? Phone { get; private set; }

    /// <summary>Who to call first.</summary>
    public bool IsMain { get; private set; }

    public DateTimeOffset AddedAt { get; private init; }

    /// <summary>
    /// When they stopped being there.
    /// </summary>
    /// <remarks>
    /// Kept rather than deleted, because the correspondence sent to them is still the
    /// correspondence — and "nobody is replying" is explained by this date more often than by
    /// anything else.
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

    /// <summary>
    /// Make this the one to call first, or not.
    /// </summary>
    /// <remarks>
    /// Setting it does not clear anybody else's here — the service does that, because only
    /// something holding all of a client's contacts can know which other one to unset, and an
    /// entity that tried would need to be handed its own siblings.
    /// </remarks>
    public void Main(bool isMain) => IsMain = isMain;

    /// <summary>They have left.</summary>
    public void Gone(DateTimeOffset at)
    {
        if (!IsHere)
        {
            return;
        }

        GoneAt = at;

        // Somebody who has left cannot be the first person to call, and leaving that flag
        // set is how an email goes to an address that bounces for a year.
        IsMain = false;
    }

    /// <summary>
    /// The direct line is kept out of the trail.
    /// </summary>
    /// <remarks>
    /// The same reasoning as a candidate's phone number: this is a person outside the firm,
    /// the trail is read inside it by more people than the client screen is, and nobody
    /// administering this system needs a stranger's mobile number in an audit row.
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
