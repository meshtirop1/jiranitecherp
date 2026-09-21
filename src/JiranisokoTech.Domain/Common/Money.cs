namespace JiranisokoTech.Domain.Common;

/// <summary>
/// An amount of money, stored as whole minor units with its currency.
/// </summary>
/// <remarks>
/// Two decisions are baked in here rather than left to each caller.
///
/// It is an integer count of minor units — cents, not 12.34 — because binary
/// floating point cannot represent a tenth, and an invoice total that is out by
/// a fraction of a cent is an invoice somebody has to reconcile by hand.
///
/// The currency travels with the amount and is never implicit. A system that
/// stores 1500 and looks the currency up somewhere else will eventually add
/// shillings to dollars, and the addition will succeed. Here it throws.
/// </remarks>
public readonly record struct Money : IComparable<Money>
{
    private Money(long minorUnits, string currency)
    {
        MinorUnits = minorUnits;
        Currency = currency;
    }

    /// <summary>The amount in the currency's smallest unit. 1234 KES is 12.34.</summary>
    public long MinorUnits { get; }

    /// <summary>ISO 4217, upper case. Always present.</summary>
    public string Currency { get; }

    public static Money Of(long minorUnits, string currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
        {
            throw new ArgumentException("Money must carry a currency.", nameof(currency));
        }

        var code = currency.Trim().ToUpperInvariant();

        if (code.Length != 3 || !code.All(char.IsAsciiLetterUpper))
        {
            throw new ArgumentException(
                $"'{currency}' is not an ISO 4217 code. Expected three letters, such as KES.",
                nameof(currency));
        }

        return new Money(minorUnits, code);
    }

    public static Money Zero(string currency) => Of(0, currency);

    public bool IsZero => MinorUnits == 0;

    public bool IsNegative => MinorUnits < 0;

    public static Money operator +(Money left, Money right) =>
        new(left.MinorUnits + right.SameCurrencyAs(left), left.Currency);

    public static Money operator -(Money left, Money right) =>
        new(left.MinorUnits - right.SameCurrencyAs(left), left.Currency);

    public static Money operator -(Money value) => new(-value.MinorUnits, value.Currency);

    /// <summary>
    /// Scale by a whole number — three of something, twelve months of a retainer.
    /// </summary>
    public Money Times(int factor) => new(MinorUnits * factor, Currency);

    /// <summary>
    /// Apply a rate, such as tax or a discount, rounding to the nearest minor unit.
    /// </summary>
    /// <remarks>
    /// Away-from-zero rather than banker's rounding: it is what invoices, tax
    /// authorities and anybody checking the arithmetic by hand expect, and being
    /// predictable matters more here than being statistically unbiased.
    /// </remarks>
    public Money Multiply(decimal rate) =>
        new((long)Math.Round(MinorUnits * rate, MidpointRounding.AwayFromZero), Currency);

    /// <summary>
    /// Split into parts that add back to exactly this amount.
    /// </summary>
    /// <remarks>
    /// The remainder is handed out one minor unit at a time to the earliest parts,
    /// so 10.00 into 3 gives 3.34, 3.33, 3.33 rather than three of 3.33 and a
    /// penny that has quietly evaporated.
    /// </remarks>
    public IReadOnlyList<Money> Allocate(int parts)
    {
        if (parts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(parts), "Money splits into at least one part.");
        }

        var each = MinorUnits / parts;
        var remainder = Math.Abs(MinorUnits % parts);
        var step = MinorUnits < 0 ? -1 : 1;

        var slices = new Money[parts];

        for (var i = 0; i < parts; i++)
        {
            var extra = i < remainder ? step : 0;
            slices[i] = new Money(each + extra, Currency);
        }

        return slices;
    }

    public int CompareTo(Money other) => MinorUnits.CompareTo(other.SameCurrencyAs(this));

    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    /// <summary>
    /// The guard that makes mixing currencies impossible rather than merely discouraged.
    /// </summary>
    private long SameCurrencyAs(Money other)
    {
        if (!string.Equals(Currency, other.Currency, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cannot combine {Currency} with {other.Currency}. Convert one of them first, "
                + "and record the rate you used.");
        }

        return MinorUnits;
    }

    public override string ToString() =>
        $"{Currency} {MinorUnits / 100m:0.00}";
}
