using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Platform;

/// <summary>
/// How much it matters when this stops.
/// </summary>
/// <remarks>
/// Three, and they are defined by what happens to somebody outside the firm rather than by how
/// clever the thing is. The reason to record it at all is that it is the sentence somebody needs
/// at two in the morning when two things are broken and there is one of them awake.
/// </remarks>
public enum HowCritical
{
    /// <summary>The firm stops, or a client's business does.</summary>
    Critical = 1,

    /// <summary>Somebody's day gets much harder. It can wait until morning.</summary>
    Important = 2,

    /// <summary>Internal, or there is a way round it.</summary>
    Minor = 3,
}

/// <summary>
/// Something the firm runs that can be named and can stop.
/// </summary>
/// <remarks>
/// Section 14's catalogue, and the thing section 27 said it was missing: an incident's "what it
/// affects" was free text, with a note saying a service catalogue would change that. This is
/// that change.
///
/// <b>Named after what people call it</b> — the despatch board, the tracker consumer — rather
/// than after a container, a repository or a hostname. Those are all things it is made of, and
/// they change; what a client rings up about does not.
///
/// <b>It is called Service and not System</b>, which is not only taste. A type called
/// <c>System</c> shadows the <c>System</c> namespace for every file in the namespace that holds
/// it, and the failure is silent until somebody writes <c>System.Math</c> and gets an error that
/// makes no sense. <see cref="Domain.Engineering.DeploymentEnvironment"/> is named the way it is
/// for exactly this reason, and <c>Money</c> did it to this codebase three times before it was
/// caught.
///
/// <b>An owner is a person, not a team.</b> A team owns nothing at two in the morning.
/// </remarks>
public sealed class Service : Entity, IAuditable
{
    private Service()
    {
        Name = string.Empty;
        Description = string.Empty;
    }

    private Service(
        string name,
        string description,
        HowCritical matters,
        Guid? ownerId,
        Guid? repositoryId,
        DateTimeOffset at)
    {
        Name = Required(name, nameof(name));
        Description = description?.Trim() ?? string.Empty;
        Matters = matters;
        OwnerId = ownerId;
        RepositoryId = repositoryId;
        AddedAt = at;
    }

    public static Service Add(
        string name,
        string description,
        HowCritical matters,
        DateTimeOffset at,
        Guid? ownerId = null,
        Guid? repositoryId = null) =>
        new(name, description, matters, ownerId, repositoryId, at);

    public string Name { get; private set; }

    /// <summary>What it does, for somebody who has just been handed the pager.</summary>
    public string Description { get; private set; }

    public HowCritical Matters { get; private set; }

    /// <summary>Who to ask. One person.</summary>
    public Guid? OwnerId { get; private set; }

    /// <summary>
    /// The code it is built from, when it is one repository.
    /// </summary>
    /// <remarks>
    /// Optional and single, because most of this firm's services are one repository and the ones
    /// that are not would need a join table to say so badly. What it buys is the step from "the
    /// despatch board is down" to the commits and deployments that might have done it, which is
    /// the one link section 91's chain could not make from an incident.
    /// </remarks>
    public Guid? RepositoryId { get; private set; }

    public DateTimeOffset AddedAt { get; private init; }

    /// <summary>When the firm stopped running it, if it has.</summary>
    /// <remarks>
    /// Retired rather than deleted, because incidents point at it. A service deleted out of the
    /// catalogue would take the name off every incident it ever had, which is the history
    /// somebody reads to decide whether to rebuild the thing.
    /// </remarks>
    public DateTimeOffset? RetiredAt { get; private set; }

    public string? Notes { get; private set; }

    public bool IsLive => RetiredAt is null;

    public void Describe(
        string name,
        string description,
        HowCritical matters,
        Guid? ownerId,
        Guid? repositoryId,
        string? notes)
    {
        Name = Required(name, nameof(name));
        Description = description?.Trim() ?? string.Empty;
        Matters = matters;
        OwnerId = ownerId;
        RepositoryId = repositoryId;
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
    }

    public void Retire(DateTimeOffset at) => RetiredAt ??= at;

    public void StillRunning() => RetiredAt = null;

    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private static string Required(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();
}

/// <summary>
/// What sort of thing a resource is.
/// </summary>
/// <remarks>
/// The brief names servers, cloud, containers, databases, DNS, domains and SSL, and they are one
/// table rather than seven because everything this firm needs from them is the same: what is it,
/// who provides it, which environment is it part of, what does it belong to, and when does it
/// expire. Seven tables would be seven screens differing only in their headings.
/// </remarks>
public enum ResourceKind
{
    /// <summary>A machine, virtual or otherwise.</summary>
    Server = 1,

    /// <summary>A managed database, or one the firm runs itself.</summary>
    Database = 2,

    /// <summary>Somewhere containers run: a cluster, an app platform, a single host.</summary>
    ContainerHost = 3,

    /// <summary>Object storage, a bucket, a file share.</summary>
    Storage = 4,

    /// <summary>A registered domain name.</summary>
    Domain = 5,

    /// <summary>A DNS zone, when it is somewhere other than the registrar.</summary>
    DnsZone = 6,

    /// <summary>A TLS certificate.</summary>
    Certificate = 7,

    /// <summary>A paid service the firm depends on and did not build.</summary>
    ThirdParty = 8,

    Other = 9,
}

/// <summary>
/// Something the firm runs on, rents or has registered.
/// </summary>
/// <remarks>
/// Section 14. <b>It records, and it discovers nothing.</b> Nothing here talks to a cloud API,
/// reads a DNS zone or opens a TLS connection to check a certificate — every row is something a
/// person typed. A register that claimed otherwise would be trusted and would be wrong: the
/// moment credentials expired or an account was renamed it would go quietly stale while still
/// looking authoritative.
///
/// <b>The expiry date is the part that acts.</b> Everything else here is a note to whoever is
/// looking; a domain that lapses takes the firm's email with it, and a certificate that expires
/// takes the site down at a moment nobody chose. That column is what the reminder job reads, on
/// the same ladder contracts and qualifications already use.
///
/// <b>The environment is the one from deployments</b> rather than a second spelling of the same
/// idea, so "what is production" means the same thing on this screen as on the one that says
/// what is deployed where.
/// </remarks>
public sealed class Resource : Entity, IAuditable
{
    private Resource()
    {
        Name = string.Empty;
    }

    private Resource(
        string name,
        ResourceKind kind,
        string? provider,
        Engineering.DeploymentEnvironment environment,
        Guid? serviceId,
        DateOnly? expiresOn,
        DateTimeOffset at)
    {
        Name = Required(name, nameof(name));
        Kind = kind;
        Provider = Trimmed(provider);
        Environment = environment;
        ServiceId = serviceId;
        ExpiresOn = expiresOn;
        AddedAt = at;
    }

    public static Resource Record(
        string name,
        ResourceKind kind,
        Engineering.DeploymentEnvironment environment,
        DateTimeOffset at,
        string? provider = null,
        Guid? serviceId = null,
        DateOnly? expiresOn = null) =>
        new(name, kind, provider, environment, serviceId, expiresOn, at);

    /// <summary>The hostname, the domain, the cluster's name — whatever it answers to.</summary>
    public string Name { get; private set; }

    public ResourceKind Kind { get; private set; }

    /// <summary>Who provides it, in their own name. Free text, because the list is endless.</summary>
    public string? Provider { get; private set; }

    public Engineering.DeploymentEnvironment Environment { get; private set; }

    /// <summary>What it is part of, when it is part of something.</summary>
    public Guid? ServiceId { get; private set; }

    /// <summary>
    /// When it lapses: a registration, a renewal, a certificate's not-after.
    /// </summary>
    /// <remarks>
    /// Optional, because a server does not expire. Where it is set it is the only field here
    /// that anything reads on its own — see <c>WarnAboutExpiringResources</c>.
    /// </remarks>
    public DateOnly? ExpiresOn { get; private set; }

    /// <summary>Where to find it: a console link, a dashboard, an address.</summary>
    public string? Address { get; private set; }

    public string? Notes { get; private set; }

    public DateTimeOffset AddedAt { get; private init; }

    public DateTimeOffset? RetiredAt { get; private set; }

    public bool IsLive => RetiredAt is null;

    public bool HasExpired(DateOnly on) => ExpiresOn is { } ends && on > ends;

    public bool ExpiresWithin(DateOnly on, int days) =>
        ExpiresOn is { } ends && ends >= on && ends <= on.AddDays(days);

    public void Describe(
        string name,
        ResourceKind kind,
        string? provider,
        Engineering.DeploymentEnvironment environment,
        Guid? serviceId,
        DateOnly? expiresOn,
        string? address,
        string? notes)
    {
        Name = Required(name, nameof(name));
        Kind = kind;
        Provider = Trimmed(provider);
        Environment = environment;
        ServiceId = serviceId;
        ExpiresOn = expiresOn;
        Address = Trimmed(address);
        Notes = Trimmed(notes);
    }

    /// <summary>
    /// The firm no longer has it.
    /// </summary>
    /// <remarks>
    /// Kept rather than deleted, and for a reason peculiar to this table: a server that was
    /// decommissioned is the answer to "what used to be at that address", which is a question
    /// somebody asks precisely when something unexpected is still talking to it.
    /// </remarks>
    public void Retire(DateTimeOffset at) => RetiredAt ??= at;

    public void StillThere() => RetiredAt = null;

    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private static string Required(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
