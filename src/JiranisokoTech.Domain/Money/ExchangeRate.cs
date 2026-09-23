using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Money;

/// <summary>
/// What one currency was worth in another, on a day.
/// </summary>
/// <remarks>
/// Section 57 asked for multi-currency, and <see cref="Common.Money"/> already refuses
/// to add shillings to dollars, which is the half that prevents silent errors. This is
/// the other half: turning one into the other when somebody genuinely needs a total
/// across both.
///
/// Three decisions here are what separate a usable rate table from a dangerous one.
///
/// <b>A rate is dated and never current.</b> There is no "the exchange rate" — only
/// what it was on a day. An invoice raised in dollars in March and converted at
/// September's rate is misreported by however much the shilling moved in between, and
/// the misreporting is invisible because both numbers look like money.
///
/// <b>A conversion is a report, never a record.</b> Nothing in this system stores a
/// converted amount. An invoice in dollars stays an amount in dollars forever, because
/// that is what the client owes; the shilling figure exists only in whatever report
/// asked for it, computed from the rate on the date that report is about. Storing the
/// converted figure would mean two amounts that disagree the moment a rate is
/// corrected, and no way to tell which one anybody acted on.
///
/// <b>Rates are entered, not fetched.</b> No live feed, deliberately. A feed means a
/// report whose numbers change between two readings for reasons nobody in the room can
/// explain, and an outage means a report that cannot be produced at all. A month-end
/// rate typed in from the central bank's published figure is auditable, repeatable, and
/// the same thing the accountant is using.
/// </remarks>
public sealed class ExchangeRate : Entity, IAuditable
{
    private ExchangeRate()
    {
        From = string.Empty;
        To = string.Empty;
    }

    private ExchangeRate(string from, string to, decimal rate, DateOnly on, string? source)
    {
        From = Iso(from, nameof(from));
        To = Iso(to, nameof(to));

        if (From == To)
        {
            throw new ArgumentException(
                "A currency is worth one of itself. There is nothing to record.", nameof(to));
        }

        if (rate <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rate),
                "A rate has to be greater than nothing. A zero rate would make every amount "
                + "it touched disappear, and a negative one would make money owed into money "
                + "owing.");
        }

        Rate = rate;
        On = on;
        Source = string.IsNullOrWhiteSpace(source) ? null : source.Trim();
    }

    /// <summary>Record what one unit of <paramref name="from"/> bought.</summary>
    public static ExchangeRate Record(
        string from, string to, decimal rate, DateOnly on, string? source = null) =>
        new(from, to, rate, on, source);

    /// <summary>The currency being converted out of, ISO 4217.</summary>
    public string From { get; private init; }

    public string To { get; private init; }

    /// <summary>
    /// How many units of <see cref="To"/> one unit of <see cref="From"/> bought.
    /// </summary>
    /// <remarks>
    /// A decimal rather than a double, for the same reason Money counts minor units: a
    /// rate of 129.45 is not representable in binary floating point, and a rate applied
    /// to a year of invoices compounds the error into a figure somebody has to
    /// reconcile.
    ///
    /// The direction is stated in the property names because getting it backwards is the
    /// classic mistake here, and the result is wrong by a factor of sixteen thousand
    /// rather than by a rounding — which at least means somebody notices.
    /// </remarks>
    public decimal Rate { get; private init; }

    /// <summary>The day this was the rate.</summary>
    public DateOnly On { get; private init; }

    /// <summary>
    /// Where the figure came from.
    /// </summary>
    /// <remarks>
    /// Free text, and worth having. "Central Bank of Kenya, mean rate" is the difference
    /// between a number an auditor accepts and a number somebody has to justify from
    /// memory two years later.
    /// </remarks>
    public string? Source { get; private init; }

    /// <summary>
    /// Convert an amount, or refuse.
    /// </summary>
    /// <remarks>
    /// Refuses an amount in the wrong currency rather than assuming. A conversion that
    /// silently treated dollars as shillings because the rate happened to be loaded
    /// would produce a plausible number, and a plausible wrong number is worse than an
    /// exception.
    ///
    /// Rounds to the nearest minor unit, away from zero on a tie. Banker's rounding is
    /// defensible and is not what a person checking the arithmetic by hand will do, and
    /// the whole value of a converted figure is that somebody can check it.
    /// </remarks>
    public Common.Money Apply(Common.Money amount)
    {
        if (amount.Currency != From)
        {
            throw new InvalidOperationException(
                $"This rate converts {From} and the amount is in {amount.Currency}. Converting "
                + "it anyway would produce a number that looks like money and is not.");
        }

        var converted = decimal.Round(
            amount.MinorUnits * Rate, 0, MidpointRounding.AwayFromZero);

        return Common.Money.Of((long)converted, To);
    }

    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private static string Iso(string value, string parameter)
    {
        var trimmed = value?.Trim().ToUpperInvariant();

        return trimmed is { Length: 3 } && trimmed.All(char.IsAsciiLetterUpper)
            ? trimmed
            : throw new ArgumentException(
                "A currency is three letters, such as KES or USD.", parameter);
    }
}
