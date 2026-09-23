namespace JiranisokoTech.Web.Api;

/// <summary>
/// How much of a list a caller gets, and how it asks for the next part.
/// </summary>
/// <remarks>
/// Section 39 had a read-only API with no paging, which is a promise the firm cannot keep.
/// Every list endpoint returned everything: fine with forty clients, and an answer that
/// takes twenty seconds and several megabytes with forty thousand. The caller is on
/// somebody else's server with its own timeout, so the first sign of trouble is an
/// integration that simply stops working, at a size nobody chose.
///
/// <b>Offset paging, not a cursor.</b> A cursor is better under a list that changes while
/// being read, and it costs a stable sort key exposed to the caller, an opaque token to
/// encode, and an explanation of why page four cannot be jumped to. These lists are
/// clients, projects and invoices — tens of thousands at most, changing a few times an
/// hour — and a caller asking for page four of its own client list wants page four. The
/// cost of the simpler thing is a row that moves between requests appearing twice or not
/// at all, which for these lists is a cosmetic fault and not a correctness one.
///
/// <b>A limit is always applied, whether or not one was asked for.</b> A default of
/// "everything" is how the whole problem got here.
/// </remarks>
public sealed record Paging
{
    /// <summary>How many a caller gets when it does not say.</summary>
    /// <remarks>
    /// Fifty. Enough to be useful in one request for anybody exploring the API by hand,
    /// small enough that a caller which ignores paging entirely still gets a fast answer
    /// rather than a slow one it will blame on the network.
    /// </remarks>
    public const int Default = 50;

    /// <summary>
    /// The most a caller may ask for.
    /// </summary>
    /// <remarks>
    /// Two hundred, and the cap is the point of the whole file: without it, `?take=100000`
    /// is the same unbounded query with a parameter on it. A caller that wants everything
    /// walks the pages, which is slower for them and survivable for the firm.
    /// </remarks>
    public const int Most = 200;

    private Paging(int skip, int take)
    {
        Skip = skip;
        Take = take;
    }

    /// <summary>
    /// Read what the caller asked for, and refuse nothing.
    /// </summary>
    /// <remarks>
    /// Clamps rather than rejecting. A caller asking for three hundred is not making a
    /// mistake it wants a 400 about — it wants as many as it can have — and an error there
    /// buys an argument in somebody else's logs instead of an answer. A negative skip is
    /// read as none, for the same reason.
    /// </remarks>
    public static Paging From(int? skip, int? take) =>
        new(Math.Max(skip ?? 0, 0), Math.Clamp(take ?? Default, 1, Most));

    public int Skip { get; }

    public int Take { get; }

    /// <summary>
    /// One page, with enough beside it to ask for the next.
    /// </summary>
    /// <remarks>
    /// The total is included, and it costs a second query. Worth it: without a total a
    /// caller cannot show a progress bar, cannot size an array, and cannot tell "the last
    /// page" from "the server stopped early" — which is the difference between finishing an
    /// import and silently importing half of one.
    /// </remarks>
    public static Page<T> Wrap<T>(IReadOnlyList<T> all, Paging paging)
    {
        var page = all.Skip(paging.Skip).Take(paging.Take).ToList();

        return new Page<T>(page, all.Count, paging.Skip, paging.Take);
    }
}

/// <summary>One page of a list, and where the caller is in it.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, int Total, int Skip, int Take)
{
    /// <summary>Is there more after this?</summary>
    /// <remarks>
    /// Stated rather than left to be worked out from three numbers, because a caller that
    /// works it out will eventually work it out wrongly and stop one page early.
    /// </remarks>
    public bool More => Skip + Items.Count < Total;

    /// <summary>What to pass as `skip` to get the next page, or nothing.</summary>
    public int? NextSkip => More ? Skip + Take : null;

    /// <summary>
    /// Turn each row into what the API returns, keeping the envelope.
    /// </summary>
    /// <remarks>
    /// So an endpoint reads as one expression and the envelope is written once here rather
    /// than five times across the file. Five copies of "items, total, skip, take, nextSkip"
    /// is five chances for one endpoint to name a field differently, and a caller cannot
    /// see the difference until it breaks.
    /// </remarks>
    public Page<TOut> Map<TOut>(Func<T, TOut> shape) =>
        new([.. Items.Select(shape)], Total, Skip, Take);
}
