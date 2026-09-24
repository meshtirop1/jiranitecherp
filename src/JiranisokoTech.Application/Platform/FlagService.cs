using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Domain.Platform;

namespace JiranisokoTech.Application.Platform;

/// <summary>What feature flags need read and written.</summary>
public interface IFlagRepository
{
    Task<Flag?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Flag?> ByKeyAsync(string key, CancellationToken cancellationToken = default);

    Task<List<Flag>> AllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Flags moved in a window, for an incident to show what changed.
    /// </summary>
    /// <remarks>
    /// The reason flag history is kept at all. A flag is quick precisely because it leaves no
    /// trace anywhere else, so it is the change an incident is least likely to hear about and
    /// most likely to have been caused by.
    /// </remarks>
    Task<List<FlagMovement>> MovedBetweenAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);

    void Add(Flag flag);

    Task SaveAsync(CancellationToken cancellationToken = default);
}

/// <summary>One flag moving, flattened for a list that spans flags.</summary>
public sealed record FlagMovement(
    Guid FlagId,
    string Key,
    DeploymentEnvironment Environment,
    bool On,
    string Why,
    Guid? ById,
    DateTimeOffset At);

/// <summary>
/// Things the firm can turn on and off without deploying.
/// </summary>
/// <remarks>
/// Section 68. The flags are served over the public API so that an application can genuinely
/// use them; what this is not is an SDK, and the honest consequence is written on the screen:
/// turning something off here does not turn it off in the application until the application
/// next asks.
/// </remarks>
public sealed class FlagService(IFlagRepository flags, IClock clock)
{
    public async Task<Flag> AddAsync(
        string key, string description, CancellationToken cancellationToken = default)
    {
        var flag = Flag.Add(key, description, clock.Now);

        if (await flags.ByKeyAsync(flag.Key, cancellationToken) is not null)
        {
            throw new InvalidOperationException(
                $"There is already a flag called {flag.Key}. Two with one key means an "
                + "application gets whichever the database returned first.");
        }

        flags.Add(flag);
        await flags.SaveAsync(cancellationToken);

        return flag;
    }

    public async Task DescribeAsync(
        Guid id, string description, CancellationToken cancellationToken = default)
    {
        var flag = await Required(id, cancellationToken);

        flag.Describe(description);

        await flags.SaveAsync(cancellationToken);
    }

    public async Task SetAsync(
        Guid id,
        DeploymentEnvironment environment,
        bool on,
        string why,
        Guid? byId,
        CancellationToken cancellationToken = default)
    {
        var flag = await Required(id, cancellationToken);

        flag.Set(environment, on, why, byId, clock.Now);

        await flags.SaveAsync(cancellationToken);
    }

    public async Task RetireAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var flag = await Required(id, cancellationToken);

        flag.Retire(clock.Now);

        await flags.SaveAsync(cancellationToken);
    }

    public async Task InUseAgainAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var flag = await Required(id, cancellationToken);

        flag.InUseAgain();

        await flags.SaveAsync(cancellationToken);
    }

    public Task<List<Flag>> AllAsync(CancellationToken cancellationToken = default) =>
        flags.AllAsync(cancellationToken);

    public Task<Flag?> OneAsync(Guid id, CancellationToken cancellationToken = default) =>
        flags.FindAsync(id, cancellationToken);

    /// <summary>
    /// What an application asks for: every live flag's state in one environment.
    /// </summary>
    /// <remarks>
    /// Retired flags are left out, which is the one decision in this method. An application
    /// still asking for a retired flag gets nothing back and falls to its own default — which
    /// is what a flag's absence has to mean, and is why retiring is not deleting: the row stays
    /// so that anybody reading the history can see what happened to it.
    /// </remarks>
    public async Task<Dictionary<string, bool>> ForAsync(
        DeploymentEnvironment environment, CancellationToken cancellationToken = default)
    {
        var all = await flags.AllAsync(cancellationToken);

        return all
            .Where(one => one.IsLive)
            .ToDictionary(one => one.Key, one => one.IsOn(environment), StringComparer.Ordinal);
    }

    /// <summary>Flags moved in a window, for an incident's "what changed just before".</summary>
    public Task<List<FlagMovement>> MovedBetweenAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default) =>
        flags.MovedBetweenAsync(from, to, cancellationToken);

    private async Task<Flag> Required(Guid id, CancellationToken cancellationToken) =>
        await flags.FindAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("There is no flag with that identifier.");
}
