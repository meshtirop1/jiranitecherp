using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Engineering;

/// <summary>Where the code is kept.</summary>
/// <remarks>
/// An enum rather than a string, because each one means a different payload
/// shape, a different signature scheme and a different adapter. A provider
/// nothing can parse is a repository that silently receives nothing.
/// </remarks>
public enum GitProvider
{
    GitHub = 1,
    GitLab = 2,
    Bitbucket = 3,
    AzureDevOps = 4,
}

/// <summary>
/// A code repository this firm watches.
/// </summary>
/// <remarks>
/// The point of this record is not to hold a URL somebody can click. It is the
/// thing an incoming webhook is matched against: a delivery arrives naming an
/// owner and a repository, and this is how the system decides whether it is
/// ours, which project it belongs to, and which secret its signature should
/// have been computed with.
///
/// The brief's central promise depends on it. Developers are not supposed to
/// report that they pushed, opened a pull request or merged one — the system is
/// supposed to already know, because the repository told it.
/// </remarks>
public sealed class Repository : Entity, IAuditable
{
    private Repository()
    {
        Owner = string.Empty;
        Name = string.Empty;
        SecretHash = string.Empty;
    }

    private Repository(
        GitProvider provider,
        string owner,
        string name,
        Guid? projectId,
        string secretHash,
        DateTimeOffset at)
    {
        Provider = provider;
        Owner = Required(owner, nameof(owner));
        Name = Required(name, nameof(name));
        ProjectId = projectId;
        SecretHash = Required(secretHash, nameof(secretHash));
        ConnectedAt = at;

        Raise(new RepositoryConnected(Id, provider, FullName, projectId, at));
    }

    public static Repository Connect(
        GitProvider provider,
        string owner,
        string name,
        Guid? projectId,
        string secretHash,
        DateTimeOffset at) =>
        new(provider, owner, name, projectId, secretHash, at);

    public GitProvider Provider { get; private init; }

    /// <summary>The account or organisation the repository sits under.</summary>
    public string Owner { get; private init; }

    public string Name { get; private init; }

    /// <summary>How the provider names it, and how a delivery identifies it.</summary>
    public string FullName => $"{Owner}/{Name}";

    /// <summary>
    /// The project whose work this repository holds, if it is one project's.
    /// </summary>
    /// <remarks>
    /// Optional, because a shared library belongs to the firm rather than to a
    /// project, and refusing to watch one until somebody invents a project for
    /// it would mean the commits that touch everything are the commits nobody
    /// records.
    /// </remarks>
    public Guid? ProjectId { get; private set; }

    /// <summary>
    /// A hash of the secret the provider signs its deliveries with.
    /// </summary>
    /// <remarks>
    /// Only a hash is kept, for the same reason an API key keeps only a hash:
    /// a copy of this table should not be a set of working credentials.
    ///
    /// The consequence is deliberate and worth stating. A signature cannot be
    /// verified from a hash — verification needs the secret itself — so the
    /// secret lives in configuration, per provider, and this hash exists to
    /// prove that the value in configuration is the one this repository was
    /// connected with. If somebody rotates the secret at the provider and not
    /// here, deliveries start failing verification loudly rather than being
    /// accepted on trust.
    /// </remarks>
    public string SecretHash { get; private init; }

    public DateTimeOffset ConnectedAt { get; private init; }

    public DateTimeOffset? LastDeliveryAt { get; private set; }

    public DateTimeOffset? DisconnectedAt { get; private set; }

    public bool IsWatched => DisconnectedAt is null;

    /// <summary>Note that the provider is still talking to us.</summary>
    /// <remarks>
    /// The single most useful thing on this record when an integration goes
    /// quiet. "Connected" and "last heard from three weeks ago" are different
    /// states, and only the second one tells somebody to go and look at the
    /// webhook configuration.
    /// </remarks>
    public void Heard(DateTimeOffset at) => LastDeliveryAt = at;

    public void MoveTo(Guid? projectId) => ProjectId = projectId;

    public void Disconnect(DateTimeOffset at)
    {
        if (!IsWatched)
        {
            return;
        }

        DisconnectedAt = at;

        Raise(new RepositoryDisconnected(Id, FullName, at));
    }

    /// <summary>
    /// The hash is excluded and nothing else is.
    /// </summary>
    /// <remarks>
    /// Not because it is a secret — it is a hash — but because copying it into
    /// the trail spreads the one value worth attacking into a second table with
    /// different retention.
    /// </remarks>
    public static IReadOnlySet<string> AuditExcludes { get; } =
        new HashSet<string> { nameof(SecretHash) };

    private static string Required(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();
}

public sealed record RepositoryConnected(
    Guid RepositoryId,
    GitProvider Provider,
    string FullName,
    Guid? ProjectId,
    DateTimeOffset At) : DomainEvent;

public sealed record RepositoryDisconnected(
    Guid RepositoryId, string FullName, DateTimeOffset At) : DomainEvent;
