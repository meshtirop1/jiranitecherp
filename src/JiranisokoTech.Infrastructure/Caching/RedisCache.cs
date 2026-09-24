using System.Text.Json;
using JiranisokoTech.Application.Abstractions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JiranisokoTech.Infrastructure.Caching;

/// <summary>What the cache needs told.</summary>
/// <remarks>
/// A blank <see cref="Connection"/> means the feature is off rather than misconfigured — the same
/// reading <c>Metrics:Token</c> and the mail transport already have, so a developer running this
/// without Redis gets an application that works rather than one that refuses to start.
/// </remarks>
public sealed class CacheOptions
{
    public const string Section = "Cache";

    /// <summary>Where Redis is. Blank turns the cache off.</summary>
    public string Connection { get; set; } = string.Empty;

    /// <summary>
    /// How long the careers list may be stale.
    /// </summary>
    /// <remarks>
    /// Sixty seconds, and it is the correctness guarantee rather than a tuning knob — see
    /// <c>ForgetTheOpeningsWhenAnAdvertMoves</c> for why eviction alone cannot be one. The number
    /// lives here because the careers page says it out loud, and a sentence on a screen that
    /// disagrees with a constant in code is worse than no sentence.
    /// </remarks>
    public TimeSpan OpeningsLife { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// The cache, over Redis.
/// </summary>
/// <remarks>
/// The adapter beside <c>FileDocumentStore</c>, <c>FileCvStore</c> and
/// <c>DataProtectionSecretStore</c> — every Redis call in the application is inside this class,
/// so the application layer never learns what the cache is made of.
///
/// <b>Every Redis operation is inside a broad catch.</b> CLAUDE.md's rule is that a handler
/// crossing into infrastructure catches broadly and says which half happened; here the answer is
/// always the same and always safe — fall through to the factory and serve the truth slowly. An
/// application that stops working because a cache is down has made itself less reliable by adding
/// a cache, which is the opposite of the point.
///
/// <b>A miss and a fault are counted apart.</b> A fault silently counted as a miss is a Redis
/// that has been down for a week behind a hit rate that merely looks poor.
/// </remarks>
public sealed class RedisCache(
    IDistributedCache cache,
    ILogger<RedisCache> log) : ICache
{
    /// <summary>
    /// Bumped when the shape of anything cached changes.
    /// </summary>
    /// <remarks>
    /// Old bytes deserialised into a new type is a fault that survives a deployment and shows up
    /// as a page rendering nonsense rather than as an error. A version in the key means the old
    /// entries are simply never looked at again, and expire on their own.
    /// </remarks>
    private const string Version = "v1";

    public async Task<T> GetOrSetAsync<T>(
        string key,
        TimeSpan life,
        Func<CancellationToken, Task<T>> produce,
        CancellationToken cancellationToken = default)
    {
        var full = $"{Version}:{key}";

        try
        {
            var kept = await cache.GetStringAsync(full, cancellationToken);

            if (kept is not null)
            {
                var value = JsonSerializer.Deserialize<T>(kept);

                if (value is not null)
                {
                    return value;
                }
            }
        }
        catch (Exception exception)
        {
            /*
             * Includes a deserialisation failure as well as a connection one, deliberately. Both
             * mean the same thing to a caller — the kept answer is unusable — and both must end
             * with the truth being computed rather than with an exception reaching a page.
             */
            log.LogWarning(
                exception,
                "The cache could not be read for {Key}. The answer will be computed instead.",
                full);
        }

        var fresh = await produce(cancellationToken);

        try
        {
            await cache.SetStringAsync(
                full,
                JsonSerializer.Serialize(fresh),
                new DistributedCacheEntryOptions
                {
                    // Absolute, never sliding. A key kept alive by traffic has no bound on its
                    // staleness, and the careers page is precisely the one with constant traffic
                    // — sliding expiry would make the busiest page the stalest.
                    AbsoluteExpirationRelativeToNow = life,
                },
                cancellationToken);
        }
        catch (Exception exception)
        {
            log.LogWarning(
                exception, "The cache could not be written for {Key}.", full);
        }

        return fresh;
    }

    public async Task ForgetAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            await cache.RemoveAsync($"{Version}:{key}", cancellationToken);
        }
        catch (Exception exception)
        {
            /*
             * Swallowed, because the lifetime is the guarantee and this is only a latency
             * improvement. An eviction that fails costs at most one stale minute; an eviction
             * that throws would take down the outbox handler that called it, and with it every
             * other handler in that batch.
             */
            log.LogWarning(
                exception, "The cache could not be cleared for {Key}.", key);
        }
    }
}

/// <summary>
/// The cache when there is no Redis.
/// </summary>
/// <remarks>
/// Chosen once at startup from configuration, the way the mail transport is — never per call,
/// which would leave a code path that only ever runs in production.
///
/// <b>It holds nothing.</b> Not a dictionary: one shared dictionary across the test host
/// instances in a single process would serve one test's answer to another, whose database is a
/// different database. A cache that makes tests pass in isolation and fail together is worse than
/// no cache, and the thing being tested here is never the caching.
/// </remarks>
public sealed class NoCache : ICache
{
    public Task<T> GetOrSetAsync<T>(
        string key,
        TimeSpan life,
        Func<CancellationToken, Task<T>> produce,
        CancellationToken cancellationToken = default) =>
        produce(cancellationToken);

    public Task ForgetAsync(string key, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
