using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Infrastructure.Caching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;

namespace JiranisokoTech.Tests.Infrastructure;

/// <summary>
/// The cache, and the promises it has to keep when Redis does not.
/// </summary>
/// <remarks>
/// Section 43. This codebase spent section 18 refusing stored figures — "a stored balance is a
/// number that can disagree with the documents it was added up from" — and a cache is exactly
/// such a figure. So the tests here are not about whether caching is fast. They are about the
/// three properties that make it safe to have at all: it never throws for a cache reason, it
/// never serves an answer past its lifetime, and with no Redis configured it holds nothing.
/// </remarks>
public class CachingTests
{
    /// <summary>
    /// A cache that cannot be read still answers, from the factory.
    /// </summary>
    /// <remarks>
    /// The property the whole section rests on. An application that stops working because a cache
    /// is down has made itself less reliable by adding a cache, which is the opposite of the
    /// point — and it is the failure mode nobody sees until Redis falls over in production.
    /// </remarks>
    [Fact]
    public async Task A_cache_that_throws_on_read_still_answers()
    {
        var cache = new RedisCache(new BrokenCache(), NullLogger<RedisCache>.Instance);

        var answer = await cache.GetOrSetAsync(
            "anything", TimeSpan.FromMinutes(1), _ => Task.FromResult("the truth"));

        Assert.Equal("the truth", answer);
    }

    /// <summary>A cache that cannot be written still answers, and does not throw.</summary>
    [Fact]
    public async Task A_cache_that_throws_on_write_still_answers()
    {
        var cache = new RedisCache(
            new BrokenCache(onReadOnly: false), NullLogger<RedisCache>.Instance);

        var answer = await cache.GetOrSetAsync(
            "anything", TimeSpan.FromMinutes(1), _ => Task.FromResult(41 + 1));

        Assert.Equal(42, answer);
    }

    /// <summary>Clearing a key that cannot be reached is not an error either.</summary>
    /// <remarks>
    /// Eviction runs from an outbox handler. An eviction that threw would take down the handler
    /// that called it, and with it every other handler in that batch — to save at most one stale
    /// minute, since the lifetime is the guarantee and this is only latency.
    /// </remarks>
    [Fact]
    public async Task Clearing_a_key_that_cannot_be_reached_does_not_throw()
    {
        var cache = new RedisCache(new BrokenCache(), NullLogger<RedisCache>.Instance);

        await cache.ForgetAsync("anything");
    }

    /// <summary>
    /// A kept answer is served again; a cleared one is computed again.
    /// </summary>
    /// <remarks>
    /// The ordinary path, over an in-memory distributed cache rather than a real Redis — what is
    /// being tested is this class's own logic, and a test that needed a running Redis would be
    /// one nobody runs.
    /// </remarks>
    [Fact]
    public async Task An_answer_is_kept_and_can_be_thrown_away()
    {
        var cache = new RedisCache(new InMemoryCache(), NullLogger<RedisCache>.Instance);
        var produced = 0;

        Task<int> Produce(CancellationToken token)
        {
            produced++;

            return Task.FromResult(produced);
        }

        Assert.Equal(1, await cache.GetOrSetAsync("k", TimeSpan.FromMinutes(1), Produce));
        Assert.Equal(1, await cache.GetOrSetAsync("k", TimeSpan.FromMinutes(1), Produce));
        Assert.Equal(1, produced);

        await cache.ForgetAsync("k");

        Assert.Equal(2, await cache.GetOrSetAsync("k", TimeSpan.FromMinutes(1), Produce));
        Assert.Equal(2, produced);
    }

    /// <summary>
    /// The lifetime handed to the cache is absolute, never sliding.
    /// </summary>
    /// <remarks>
    /// A key kept alive by traffic has no bound on its staleness, and the careers page is exactly
    /// the one with constant traffic — sliding expiry would make the busiest page the stalest.
    /// Asserted on what is handed to the store, because the alternative is a test that sleeps.
    /// </remarks>
    [Fact]
    public async Task The_lifetime_is_absolute_rather_than_sliding()
    {
        var store = new InMemoryCache();
        var cache = new RedisCache(store, NullLogger<RedisCache>.Instance);

        await cache.GetOrSetAsync(
            "k", TimeSpan.FromSeconds(60), _ => Task.FromResult("v"));

        Assert.Equal(TimeSpan.FromSeconds(60), store.LastOptions?.AbsoluteExpirationRelativeToNow);
        Assert.Null(store.LastOptions?.SlidingExpiration);
    }

    /// <summary>
    /// With no Redis configured, nothing is kept at all.
    /// </summary>
    /// <remarks>
    /// Deliberately not an in-memory dictionary. One shared dictionary across the test host
    /// instances in a single process would serve one test's answer to another whose database is a
    /// different database — a cache that makes tests pass alone and fail together, hiding the
    /// thing actually under test.
    /// </remarks>
    [Fact]
    public async Task With_no_redis_nothing_is_kept()
    {
        ICache cache = new NoCache();
        var produced = 0;

        Task<int> Produce(CancellationToken token) => Task.FromResult(++produced);

        await cache.GetOrSetAsync("k", TimeSpan.FromMinutes(1), Produce);
        await cache.GetOrSetAsync("k", TimeSpan.FromMinutes(1), Produce);

        Assert.Equal(2, produced);
    }

    /// <summary>A distributed cache where every operation fails.</summary>
    private sealed class BrokenCache(bool onReadOnly = true) : IDistributedCache
    {
        public byte[]? Get(string key) => throw new InvalidOperationException("no redis");

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) =>
            throw new InvalidOperationException("no redis");

        public void Refresh(string key) => throw new InvalidOperationException("no redis");

        public Task RefreshAsync(string key, CancellationToken token = default) =>
            throw new InvalidOperationException("no redis");

        public void Remove(string key) => throw new InvalidOperationException("no redis");

        public Task RemoveAsync(string key, CancellationToken token = default) =>
            throw new InvalidOperationException("no redis");

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) =>
            throw new InvalidOperationException("no redis");

        public Task SetAsync(
            string key,
            byte[] value,
            DistributedCacheEntryOptions options,
            CancellationToken token = default) =>
            onReadOnly
                ? throw new InvalidOperationException("no redis")
                : throw new InvalidOperationException("no redis on write");
    }

    /// <summary>A distributed cache in a dictionary, remembering the last options it was given.</summary>
    private sealed class InMemoryCache : IDistributedCache
    {
        private readonly Dictionary<string, byte[]> _kept = [];

        public DistributedCacheEntryOptions? LastOptions { get; private set; }

        public byte[]? Get(string key) => _kept.GetValueOrDefault(key);

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) =>
            Task.FromResult(_kept.GetValueOrDefault(key));

        public void Refresh(string key)
        {
        }

        public Task RefreshAsync(string key, CancellationToken token = default) =>
            Task.CompletedTask;

        public void Remove(string key) => _kept.Remove(key);

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            _kept.Remove(key);

            return Task.CompletedTask;
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            _kept[key] = value;
            LastOptions = options;
        }

        public Task SetAsync(
            string key,
            byte[] value,
            DistributedCacheEntryOptions options,
            CancellationToken token = default)
        {
            Set(key, value, options);

            return Task.CompletedTask;
        }
    }
}
