using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Time;

/// <summary>
/// A day the country does not work, and what it is called.
/// </summary>
/// <remarks>
/// Data somebody keeps, not a list in the source. Kenyan public holidays cannot
/// be written down once and left: Idd-ul-Fitr and Idd-ul-Azha follow the lunar
/// calendar and move by eleven days a year, Easter moves, and the government
/// declares one-off days by gazette notice — an election, a state funeral, a
/// public holiday moved to the following Monday because it fell on a Sunday. A
/// hardcoded calendar would be wrong within the year and would need a
/// deployment to correct, which means in practice it would stay wrong.
///
/// Deliberately not tied to a country or a region. This firm has one office and
/// its staff observe the Kenyan calendar, so a country column would be a column
/// holding "KE" on every row and inviting somebody to build a per-country
/// calendar nobody asked for. If the firm opens abroad, that is a real decision
/// with real questions in it — which office does a remote employee observe? —
/// and it deserves to be made then rather than guessed at now.
/// </remarks>
public sealed class Holiday : Entity, IAuditable
{
    private Holiday()
    {
        Name = string.Empty;
    }

    private Holiday(DateOnly on, string name)
    {
        On = on;
        Name = Require(name, nameof(name));
    }

    /// <summary>
    /// Put a day on the calendar.
    /// </summary>
    /// <remarks>
    /// Named for what actually happens in Kenya. A holiday here is declared —
    /// the ones everybody knows by the Public Holidays Act, and the rest by a
    /// notice somebody in the office reads and types in.
    /// </remarks>
    public static Holiday Declared(DateOnly on, string name) => new(on, name);

    public DateOnly On { get; private init; }

    /// <summary>
    /// What it is called, and it is required.
    /// </summary>
    /// <remarks>
    /// A bare date on this screen is unreviewable. "Is 1 May meant to be there?"
    /// is answerable when the row says Labour Day and not otherwise, and the one
    /// thing this calendar must support is somebody checking it before the leave
    /// year starts.
    /// </remarks>
    public string Name { get; private set; }

    /// <summary>Correct what it is called, without moving it.</summary>
    /// <remarks>
    /// The date is <c>init</c> and this does not change it. Moving a holiday
    /// would silently change the length of every leave request across it, and
    /// nothing on the row would say it had happened; withdrawing the wrong day
    /// and declaring the right one leaves two audit entries that explain each
    /// other.
    /// </remarks>
    public void Rename(string name) => Name = Require(name, nameof(name));

    /// <summary>
    /// Nothing here is hidden from the trail.
    /// </summary>
    /// <remarks>
    /// Both columns matter to it. A holiday appearing or disappearing changes
    /// how much leave people are charged, so "who put 26 December on the
    /// calendar, and when" is a question somebody in payroll will eventually
    /// ask.
    /// </remarks>
    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private static string Require(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();
}

/// <summary>
/// How many days off a stretch of the calendar actually costs somebody.
/// </summary>
/// <remarks>
/// The one definition of the rule, and the reason this is a type of its own
/// rather than a method on <see cref="LeaveRequest"/>. There were two copies
/// before this — the aggregate's and a private helper in the infrastructure's
/// leave query, which existed so that a list of thirty requests need not
/// materialise thirty aggregates to print one number each. Both counted
/// weekends and neither counted holidays, so they agreed; the moment one of
/// them learned about holidays they would not have, and the disagreement would
/// have shown up as a leave balance that differs depending on which screen is
/// open.
///
/// The holidays are a parameter rather than something this reaches for. An
/// aggregate that queried a calendar would be an aggregate that needs a
/// database to answer a question about itself, which is untestable and, worse,
/// non-deterministic — the same request would count differently on different
/// days without anybody having changed anything.
/// </remarks>
public static class WorkingDays
{
    /// <summary>
    /// Working days from one date to another, inclusive of both.
    /// </summary>
    /// <remarks>
    /// A holiday falling on a Saturday is not deducted twice. It is not a
    /// special case in the code and it should not be: each day is looked at
    /// once and either counts or does not, so a Saturday that is also Labour Day
    /// is simply a day that did not count, for the first of two reasons. Writing
    /// it as "weekdays, less the holidays in the range" instead is the version
    /// that gets this wrong, and Kenya puts a holiday on a weekend most years.
    /// </remarks>
    public static int Between(DateOnly from, DateOnly to, IReadOnlySet<DateOnly> holidays)
    {
        ArgumentNullException.ThrowIfNull(holidays);

        var days = 0;

        for (var day = from; day <= to; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || holidays.Contains(day))
            {
                continue;
            }

            days++;
        }

        return days;
    }
}
