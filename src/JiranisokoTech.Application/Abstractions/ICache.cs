namespace JiranisokoTech.Application.Abstractions;

/// <summary>
/// Somewhere to keep an answer for a little while.
/// </summary>
/// <remarks>
/// Section 43, and the shape of this interface is most of the section's reasoning.
///
/// <b>A cache is a stored figure, which is the thing this codebase spent section 18 refusing.</b>
/// <c>Account</c> says it plainly: "a stored balance is a number that can disagree with the
/// documents it was added up from, and the day it does, nobody can tell which is wrong." Every
/// money figure here is summed from the invoices and claims themselves for that reason. A cache
/// reintroduces exactly that fault — an answer that was true once, kept somewhere, and served to
/// somebody later as though it still were.
///
/// So the port is built to make the dangerous uses awkward and the safe one easy:
///
/// <b>There is no overload without a lifetime.</b> A key with no bound on its staleness is the
/// fault in its purest form, and an optional expiry is one somebody eventually omits.
///
/// <b><see cref="GetOrSetAsync"/> takes the factory rather than returning a miss.</b> A
/// get-then-set pair lets a caller read, compute, and forget to store — or worse, store something
/// computed under one reader's permissions. Handing the factory in means the cache decides when
/// it runs and nothing else can be put in.
///
/// <b>Nothing here ever throws for a cache reason.</b> Redis being down must degrade to a slower
/// application, never a broken one. A caller gets the value or whatever the factory raised, and
/// no caller writes a catch around a cache.
///
/// <b>What must never be put in it</b> is anything scoped to who is asking. The reaches in
/// <c>Reaches</c> return a per-reader set of identifiers; a key that omits the reader serves one
/// person's reach to another, and a key that includes it caches a value that changes the moment
/// somebody is added to a project. Both are wrong, and the second only looks safe.
/// </remarks>
public interface ICache
{
    /// <summary>
    /// The kept answer, or the one the factory produces, kept for next time.
    /// </summary>
    /// <param name="key">
    /// What identifies the answer. It must contain everything the answer depends on and nothing
    /// about who is asking — see the remarks on the interface.
    /// </param>
    /// <param name="life">
    /// How stale the answer may get. Absolute, never sliding: a key kept alive by traffic has no
    /// bound at all, which would make the busiest page the stalest one.
    /// </param>
    /// <param name="produce">What to run on a miss, or when the cache cannot be reached.</param>
    Task<T> GetOrSetAsync<T>(
        string key,
        TimeSpan life,
        Func<CancellationToken, Task<T>> produce,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Throw one answer away.
    /// </summary>
    /// <remarks>
    /// An improvement in latency and never the correctness guarantee — the lifetime is that.
    /// Eviction here runs from the outbox, after the commit and on a poll interval, so there is
    /// always a window in which the old answer is still being served. Anything relying on
    /// eviction being immediate would be relying on something this system does not promise.
    /// </remarks>
    Task ForgetAsync(string key, CancellationToken cancellationToken = default);
}
