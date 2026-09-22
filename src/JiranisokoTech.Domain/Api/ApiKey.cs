using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Api;

/// <summary>
/// A key another system uses to read from this one.
/// </summary>
/// <remarks>
/// Machines cannot sign in. The cookie this application issues is bound to a
/// browser session, expires, and can be challenged for a second factor — all
/// correct for a person and all useless to a nightly job on somebody else's
/// server. So a key, with the same permissions a role carries and none of its
/// own vocabulary.
///
/// Three properties matter and each is here for a reason:
///
/// The secret is never stored. What is kept is a hash of it, so a copy of this
/// table is not a set of working keys. It is shown once, at the moment it is
/// created, and this system cannot show it again — which is the point, and
/// needs saying on the page because somebody will ask.
///
/// A key carries <see cref="Scopes"/> rather than a role. A role is a
/// description of a job somebody does, and no machine does a job; a key should
/// hold the two or three permissions the integration actually needs, and giving
/// it a role would hand it every permission that role ever acquires afterwards.
///
/// A key is revoked, never deleted. The audit trail refers to it, and a
/// disappearing key turns every entry naming it into a dead reference.
/// </remarks>
public sealed class ApiKey : Entity, IAuditable
{
    /// <summary>What every secret starts with, so one is recognisable on sight.</summary>
    /// <remarks>
    /// Worth the eight characters. A key pasted into a chat message or
    /// committed to a repository is findable by its prefix — by the firm, and
    /// by the scanners the code-hosting services run, which is the difference
    /// between finding out in a minute and finding out never.
    /// </remarks>
    public const string SecretPrefix = "jts_live_";

    private readonly List<string> _scopes = [];

    private ApiKey()
    {
        Name = string.Empty;
        Hash = string.Empty;
        Hint = string.Empty;
    }

    private ApiKey(
        string name,
        string hash,
        string hint,
        IEnumerable<string> scopes,
        Guid? createdById,
        DateTimeOffset at)
    {
        Name = Required(name, nameof(name));
        Hash = Required(hash, nameof(hash));
        Hint = Required(hint, nameof(hint));
        CreatedById = createdById;
        CreatedAt = at;

        _scopes.AddRange(scopes.Where(one => !string.IsNullOrWhiteSpace(one)).Distinct());

        if (_scopes.Count == 0)
        {
            // A key with no scopes can do nothing, so it is not a key — it is a
            // mistake that will be reported as "the integration returns
            // nothing" a week later.
            throw new ArgumentException(
                "A key needs at least one permission. One that can do nothing is not a key.",
                nameof(scopes));
        }

        Raise(new ApiKeyIssued(Id, Name, _scopes.Count, at));
    }

    public static ApiKey Issue(
        string name,
        string hash,
        string hint,
        IEnumerable<string> scopes,
        Guid? createdById,
        DateTimeOffset at) =>
        new(name, hash, hint, scopes, createdById, at);

    /// <summary>What it is for, in the words of whoever made it.</summary>
    public string Name { get; private set; }

    /// <summary>The hash of the secret. The secret itself is not kept.</summary>
    public string Hash { get; private init; }

    /// <summary>
    /// The last few characters, so a key can be told apart from another.
    /// </summary>
    /// <remarks>
    /// The last rather than the first: every key starts with the same prefix,
    /// so the beginning identifies nothing. Four characters is enough to point
    /// at one row in a list of a dozen and far too few to be worth guessing.
    /// </remarks>
    public string Hint { get; private init; }

    public IReadOnlyList<string> Scopes => _scopes.ToList();

    public Guid? CreatedById { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    /// <summary>
    /// When it was last used, to the day.
    /// </summary>
    /// <remarks>
    /// To the day, deliberately. Recording the exact moment would mean a write
    /// on every single request — turning a read-only API into one that writes to
    /// the same row from every caller at once — and the question this answers is
    /// "is anything still using this key", which a date answers perfectly well.
    /// </remarks>
    public DateOnly? LastUsedOn { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public string? RevokedReason { get; private set; }

    public bool IsLive => RevokedAt is null;

    public bool Allows(string permission) => IsLive && _scopes.Contains(permission);

    /// <summary>Note that the key was used today, if that is not already noted.</summary>
    /// <returns>Whether anything changed, so the caller can skip a pointless save.</returns>
    public bool UsedOn(DateOnly day)
    {
        if (LastUsedOn == day)
        {
            return false;
        }

        LastUsedOn = day;

        return true;
    }

    public void Revoke(string reason, DateTimeOffset at)
    {
        if (!IsLive)
        {
            return;
        }

        RevokedAt = at;
        RevokedReason = Required(reason, nameof(reason));

        Raise(new ApiKeyRevoked(Id, Name, RevokedReason, at));
    }

    public void Rename(string name) => Name = Required(name, nameof(name));

    /// <summary>
    /// The hash is excluded, and nothing else is.
    /// </summary>
    /// <remarks>
    /// Not because it is a secret — it is a hash — but because copying it into
    /// the audit trail spreads the one value worth attacking into a second
    /// table with different retention. What was created, by whom, with which
    /// scopes, and when it was revoked all stay.
    /// </remarks>
    public static IReadOnlySet<string> AuditExcludes { get; } =
        new HashSet<string> { nameof(Hash) };

    private static string Required(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();
}

public sealed record ApiKeyIssued(
    Guid KeyId, string Name, int Scopes, DateTimeOffset At) : DomainEvent;

public sealed record ApiKeyRevoked(
    Guid KeyId, string Name, string Reason, DateTimeOffset At) : DomainEvent;
