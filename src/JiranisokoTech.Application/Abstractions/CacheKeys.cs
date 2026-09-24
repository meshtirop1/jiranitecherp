namespace JiranisokoTech.Application.Abstractions;

/// <summary>
/// What the cache keeps things under.
/// </summary>
/// <remarks>
/// Named here rather than beside the query that fills them, because the thing that fills a key
/// and the thing that clears it are in different layers — the careers list is read in
/// Infrastructure and cleared by a handler in Application. Two string literals that must match
/// across a layer boundary is one typo away from a cache nothing ever evicts, and nothing would
/// fail: the list would simply go stale for as long as its lifetime allows, which is the quietest
/// possible way to be wrong.
/// </remarks>
public static class CacheKeys
{
    /// <summary>The published adverts on the public careers page.</summary>
    public const string Openings = "careers:openings";
}
