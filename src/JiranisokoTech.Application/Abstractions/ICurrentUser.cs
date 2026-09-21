namespace JiranisokoTech.Application.Abstractions;

/// <summary>
/// Who is acting, for the audit trail and for authorization.
/// </summary>
/// <remarks>
/// Nullable on purpose. Plenty of real work has no human behind it — a
/// scheduled job, an inbound webhook, a migration — and an abstraction that
/// insists on a user forces those callers to invent one. An entry with no
/// actor is honest; an entry attributed to "system@localhost" is a lie that
/// looks like a person.
/// </remarks>
public interface ICurrentUser
{
    Guid? Id { get; }

    /// <summary>Display name, copied into audit entries so it survives the account.</summary>
    string? Name { get; }

    bool IsAuthenticated => Id is not null;
}
