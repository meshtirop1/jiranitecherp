using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Domain.Engineering;

namespace JiranisokoTech.Domain.Platform;

/// <summary>
/// One flag's state in one environment.
/// </summary>
/// <remarks>
/// Per environment, because that is the entire point of a flag: on in staging, off in
/// production, until somebody decides otherwise. A single boolean would make a flag a
/// deployment, which is the thing flags exist to avoid.
/// </remarks>
public sealed class FlagSetting : Entity
{
    private FlagSetting()
    {
    }

    internal FlagSetting(DeploymentEnvironment environment, bool on)
    {
        Environment = environment;
        On = on;
    }

    public DeploymentEnvironment Environment { get; private init; }

    public bool On { get; private set; }

    internal void Set(bool on) => On = on;
}

/// <summary>
/// A time somebody moved a flag, and why.
/// </summary>
/// <remarks>
/// Append-only, and the most useful thing here. "What changed just before this broke" is the
/// first question in every incident, and until section 68 the answer could include deployments
/// and releases but not the one kind of change people make precisely because it is quick and
/// leaves no trace. A flag with no history is a flag nobody can be held to.
/// </remarks>
public sealed class FlagChange : Entity
{
    private FlagChange() => Why = string.Empty;

    internal FlagChange(
        DeploymentEnvironment environment,
        bool on,
        string why,
        Guid? byId,
        DateTimeOffset at)
    {
        Environment = environment;
        On = on;
        Why = string.IsNullOrWhiteSpace(why)
            ? throw new ArgumentException("Say why it moved.", nameof(why))
            : why.Trim();
        ById = byId;
        At = at;
    }

    public DeploymentEnvironment Environment { get; private init; }

    /// <summary>What it became.</summary>
    public bool On { get; private init; }

    public string Why { get; private init; }

    public Guid? ById { get; private init; }

    public DateTimeOffset At { get; private init; }
}

/// <summary>
/// Something the firm can turn on and off without deploying.
/// </summary>
/// <remarks>
/// Section 68.
///
/// <b>This serves the flags rather than only listing them.</b> They are readable over the
/// public API at <c>/api/v1/flags</c>, with an API key, so an application can actually use them
/// — a register of flags that nothing reads would be a spreadsheet with a schema. What it is
/// not is an SDK: there is no streaming, no long poll and no local cache with an invalidation
/// protocol. An application asks, gets a map, and asks again later, which means <b>turning a
/// flag off here does not turn it off in the application until the application next looks</b>,
/// and the screen says so rather than letting somebody believe otherwise during an incident.
///
/// <b>No percentages, no targeting, no cohorts.</b> On or off, per environment. A flag that is
/// on for eleven per cent of users is an experiment, and an experiment needs a measurement to
/// mean anything — which is a different feature with a different owner, and building the half
/// of it that splits traffic without the half that reads the result is how firms end up with
/// forty flags nobody can turn off.
///
/// <b>Every change carries a reason and is kept.</b> This is the part an incident wants. The
/// reason a flag is quick is the reason it leaves no trace, and "what changed just before"
/// could see deployments and releases and not the thing somebody actually did at 02:14.
/// </remarks>
public sealed class Flag : Entity, IAuditable
{
    private readonly List<FlagSetting> _settings = [];

    private readonly List<FlagChange> _changes = [];

    private Flag()
    {
        Key = string.Empty;
        Description = string.Empty;
    }

    private Flag(string key, string description, DateTimeOffset at)
    {
        Key = Sanitised(key);
        Description = description?.Trim() ?? string.Empty;
        AddedAt = at;

        /*
         * Off everywhere to begin with, and all four environments present from the start. A
         * flag with no row for production reads as absent rather than as off, and "absent" is
         * the state an application has to guess about — which it will do differently from the
         * next application.
         */
        foreach (var environment in All)
        {
            _settings.Add(new FlagSetting(environment, false));
        }
    }

    public static Flag Add(string key, string description, DateTimeOffset at) =>
        new(key, description, at);

    /// <summary>The environments a flag always has a state in.</summary>
    public static IReadOnlyList<DeploymentEnvironment> All { get; } =
    [
        DeploymentEnvironment.Development,
        DeploymentEnvironment.Staging,
        DeploymentEnvironment.Production,
        DeploymentEnvironment.Other,
    ];

    /// <summary>
    /// What the application asks for, in dots and lower case.
    /// </summary>
    /// <remarks>
    /// Normalised on the way in — trimmed, lower-cased, spaces to dots — because this string is
    /// typed once here and written into source code somewhere else, and a flag that answers to
    /// <c>Invoices.USD</c> and not to <c>invoices.usd</c> fails in the one way nobody debugs
    /// quickly: by being off.
    /// </remarks>
    public string Key { get; private init; }

    /// <summary>What it does, for somebody deciding whether they may turn it off.</summary>
    public string Description { get; private set; }

    public DateTimeOffset AddedAt { get; private init; }

    /// <summary>When it stopped being used. A flag is meant to be temporary.</summary>
    /// <remarks>
    /// Retired rather than deleted, so an application still asking for it gets a definite answer
    /// rather than a missing key. Deleting a flag an application still reads is how a feature
    /// comes back on in production without anybody deploying anything.
    /// </remarks>
    public DateTimeOffset? RetiredAt { get; private set; }

    /// <remarks>Returns a copy — see the note on Invoice.Lines for why.</remarks>
    public IReadOnlyList<FlagSetting> Settings => _settings.ToList();

    /// <remarks>Returns a copy — see the note on Invoice.Lines for why.</remarks>
    public IReadOnlyList<FlagChange> Changes =>
        [.. _changes.OrderByDescending(one => one.At).ThenByDescending(one => one.Id)];

    public bool IsLive => RetiredAt is null;

    /// <summary>Is it on in this environment?</summary>
    public bool IsOn(DeploymentEnvironment environment) =>
        _settings.FirstOrDefault(one => one.Environment == environment)?.On ?? false;

    public void Describe(string description) =>
        Description = description?.Trim() ?? string.Empty;

    /// <summary>
    /// Turn it on or off somewhere, with the reason.
    /// </summary>
    /// <remarks>
    /// The reason is required, and it is not bureaucracy. A flag moved at two in the morning
    /// with no note is the single most confusing artefact an incident review can meet: the
    /// timeline says the harm stopped and nothing says why, and the person who did it has
    /// reasonably forgotten by the following week.
    ///
    /// Setting it to what it already is records nothing, so a screen that posts the whole form
    /// does not fill the history with changes nobody made.
    /// </remarks>
    public void Set(
        DeploymentEnvironment environment,
        bool on,
        string why,
        Guid? byId,
        DateTimeOffset at)
    {
        if (!IsLive)
        {
            throw new InvalidOperationException(
                "This flag has been retired. Bring it back before moving it, so that anybody "
                + "reading the history can see it was in use again.");
        }

        var setting = _settings.FirstOrDefault(one => one.Environment == environment);

        if (setting is null)
        {
            setting = new FlagSetting(environment, on);

            _settings.Add(setting);
        }
        else if (setting.On == on)
        {
            return;
        }
        else
        {
            setting.Set(on);
        }

        _changes.Add(new FlagChange(environment, on, why, byId, at));

        Raise(new FlagMoved(Id, Key, environment, on, at));
    }

    public void Retire(DateTimeOffset at) => RetiredAt ??= at;

    public void InUseAgain() => RetiredAt = null;

    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    /// <summary>
    /// A key an application can be relied on to reproduce.
    /// </summary>
    /// <remarks>
    /// Lower case, dots for spaces, and nothing else touched. Deliberately not a slug generator:
    /// a key is copied into source code by hand, and something that quietly rewrote
    /// <c>invoices_usd</c> into <c>invoices-usd</c> would leave the application asking for a
    /// flag that does not exist and getting back "off" without an error.
    /// </remarks>
    private static string Sanitised(string key) =>
        string.IsNullOrWhiteSpace(key)
            ? throw new ArgumentException("A flag needs a key.", nameof(key))
            : key.Trim().ToLowerInvariant().Replace(' ', '.');
}

/// <summary>
/// A flag moved.
/// </summary>
/// <remarks>
/// Carries the key rather than only the identifier, because whatever reads this months later
/// wants the string the application asks for and not a row in a table it may no longer have.
/// </remarks>
public sealed record FlagMoved(
    Guid FlagId,
    string Key,
    DeploymentEnvironment Environment,
    bool On,
    DateTimeOffset At) : DomainEvent;
