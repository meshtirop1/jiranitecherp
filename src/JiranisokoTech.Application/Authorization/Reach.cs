using System.Linq.Expressions;

namespace JiranisokoTech.Application.Authorization;

/// <summary>
/// How far somebody can see, when the answer is not "everything" or "nothing".
/// </summary>
/// <remarks>
/// Section 5 had permissions at two grains: firm-wide, and your own record. Nothing in
/// between — so a permission meaning "the projects you are on" or "your own department"
/// could be declared and granted, and the only two things a query could do with it were
/// show everything or show nothing.
///
/// Both were done. `projects.view_member` is granted to every engineer and the search
/// treated holding it as permission to find every project by name, because the
/// alternative — returning nothing — would have made the search box useless for almost
/// everybody. The permission was not decorative; it was worse, it was wrong.
///
/// This is the missing middle. A reach is either everything or a named set, and a query
/// applies it rather than deciding for itself what a narrow permission means.
/// </remarks>
public sealed record Reach
{
    private Reach(bool everything, IReadOnlySet<Guid> only)
    {
        IsEverything = everything;
        Only = only;
    }

    /// <summary>No limit. Held by whoever has the firm-wide permission.</summary>
    public static Reach Everything { get; } =
        new(true, new HashSet<Guid>());

    /// <summary>Nothing at all, which is not the same as everything.</summary>
    /// <remarks>
    /// Its own value rather than an empty set, so a caller cannot confuse "no limit" with
    /// "limited to nothing" — which is the mistake that turns a narrow permission into a
    /// wide one.
    /// </remarks>
    public static Reach Nothing { get; } = new(false, new HashSet<Guid>());

    /// <summary>Limited to these.</summary>
    public static Reach LimitedTo(IEnumerable<Guid> ids) =>
        new(false, ids.ToHashSet());

    public bool IsEverything { get; }

    /// <summary>Empty when <see cref="IsEverything"/>, and meaningless then.</summary>
    public IReadOnlySet<Guid> Only { get; }

    /// <summary>Is there any point running the query at all?</summary>
    public bool IsNothing => !IsEverything && Only.Count == 0;

    public bool Includes(Guid id) => IsEverything || Only.Contains(id);

    /// <summary>
    /// Narrow a query, or leave it alone.
    /// </summary>
    /// <remarks>
    /// The one method callers should use, because it is the one that cannot be got wrong.
    /// Writing the condition by hand at each call site is how one of them ends up applying
    /// the limit only when the set is non-empty — which quietly grants everything to
    /// somebody who should see nothing.
    ///
    /// Takes an expression rather than a delegate, and that is not a style choice: a
    /// delegate cannot be translated to SQL, so EF would either refuse the query or pull
    /// the whole table back and filter it here. The second is worse, because it works.
    /// </remarks>
    public IQueryable<T> Apply<T>(IQueryable<T> query, Expression<Func<T, Guid>> key)
    {
        if (IsEverything)
        {
            return query;
        }

        // Built by hand so the identifiers arrive as a parameter list EF can translate
        // into an IN clause, rather than as a captured closure it cannot see into.
        var contains = Expression.Call(
            typeof(Enumerable),
            nameof(Enumerable.Contains),
            [typeof(Guid)],
            Expression.Constant(Only.ToList()),
            key.Body);

        return query.Where(Expression.Lambda<Func<T, bool>>(contains, key.Parameters));
    }
}
