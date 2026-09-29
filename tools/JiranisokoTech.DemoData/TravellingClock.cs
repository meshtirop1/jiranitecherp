using JiranisokoTech.Application.Abstractions;

namespace JiranisokoTech.DemoData;

/// <summary>
/// A clock this tool moves, so a year of history can be written in a minute.
/// </summary>
/// <remarks>
/// <b>Without this the demonstration data would be useless, and it would not look useless.</b>
/// Every date in anything written through the application services comes from <c>IClock</c>, which
/// is a singleton <c>SystemClock</c> in the running application — so a seed that just called the
/// services would produce a firm where every client was taken on, every hour logged, every invoice
/// issued and every incident resolved in the same three seconds. The pages would fill up. The
/// ageing report would have one bucket, the burndown one point, the dashboard's "oldest overdue"
/// would be today, and every trend on every screen would be a flat line — which is a worse
/// demonstration than an empty database, because an empty database is honestly empty.
///
/// It reaches the audit trail as well, because <c>AppDbContext</c> takes the same clock. So the
/// history has a history: a change made in March is stamped March in the trail too.
///
/// The same shape as <c>TestClock</c> in the test project deliberately, including the note about
/// Today, so that somebody who knows one knows the other.
/// </remarks>
public sealed class TravellingClock : IClock
{
    public DateTimeOffset Now { get; set; }

    /// <summary>
    /// The same day the interface computes, stated here so callers can reach it.
    /// </summary>
    /// <remarks>
    /// <c>IClock.Today</c> is a default interface member, which means it exists on the interface
    /// and not on this type — so a caller holding a TravellingClock could not see it without a
    /// cast, which is noise at every one of the fifty call sites in this tool.
    /// </remarks>
    public DateOnly Today => DateOnly.FromDateTime(Now.Date);

    /// <summary>Stand at nine in the morning on a given day.</summary>
    /// <remarks>
    /// Nine rather than midnight, because a record stamped exactly midnight reads as generated and
    /// because several rules here are about working hours. The offset is Nairobi's, which is the
    /// one thing about these timestamps that is not arbitrary: it is where the firm is, and
    /// PostgreSQL stores the instant either way while a reader of the raw row sees the local
    /// wall-clock time somebody would have been looking at.
    /// </remarks>
    public TravellingClock MoveTo(DateOnly day)
    {
        Now = new DateTimeOffset(
            day.Year, day.Month, day.Day, 9, 0, 0, TimeSpan.FromHours(3));

        return this;
    }

    /// <summary>Move on, within the day or past it.</summary>
    public TravellingClock Advance(TimeSpan by)
    {
        Now += by;

        return this;
    }
}
