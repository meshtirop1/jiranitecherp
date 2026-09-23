using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Domain.Money;

// Currencies rather than Money, deliberately. A namespace called Money would shadow
// the Money type for every sibling namespace in this assembly — which is exactly why
// everything inside Domain.Money has to write Common.Money — and one such namespace is
// enough.
namespace JiranisokoTech.Application.Currencies;

/// <summary>Where the rates are kept.</summary>
public interface IExchangeRateRepository
{
    /// <summary>
    /// The rate for a pair on a day, or the most recent one before it.
    /// </summary>
    /// <remarks>
    /// Falling back to an earlier rate rather than refusing, because rates are typed in
    /// at month end and a report about the fifteenth would otherwise convert nothing.
    /// The date of the rate actually used is returned with it, so a report can say "at
    /// the rate of 31 August" instead of implying a precision it does not have.
    ///
    /// Never falls forward. Converting March at September's rate is the specific error
    /// this whole file exists to prevent.
    /// </remarks>
    Task<ExchangeRate?> OnOrBeforeAsync(
        string from, string to, DateOnly on, CancellationToken cancellationToken = default);

    Task<List<ExchangeRate>> RecentAsync(
        int take = 100, CancellationToken cancellationToken = default);

    Task<ExchangeRate?> ExactAsync(
        string from, string to, DateOnly on, CancellationToken cancellationToken = default);

    void Add(ExchangeRate rate);

    void Remove(ExchangeRate rate);

    Task SaveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Recording what a currency was worth, and converting with it.
/// </summary>
/// <remarks>
/// The conversion never writes anything. An invoice in dollars stays an amount in
/// dollars forever, because that is what the client owes; a shilling figure exists only
/// in the report that asked for one. Storing the converted amount would give two
/// numbers that disagree the moment a rate is corrected, with nothing to say which one
/// anybody acted on.
/// </remarks>
public sealed class ExchangeRateService(IExchangeRateRepository rates, IClock clock)
{
    /// <summary>
    /// Record a rate, replacing one already recorded for that pair and day.
    /// </summary>
    /// <remarks>
    /// Replacing rather than refusing, because the realistic case is somebody correcting
    /// a figure they mistyped — and a system that made them delete the wrong one first
    /// would be a system where the wrong one sometimes stays.
    /// </remarks>
    public async Task<ExchangeRate> RecordAsync(
        string from,
        string to,
        decimal rate,
        DateOnly on,
        string? source = null,
        CancellationToken cancellationToken = default)
    {
        if (on > clock.Today)
        {
            throw new InvalidOperationException(
                "A rate cannot be recorded for a day that has not happened. What a currency "
                + "will be worth is a forecast, and a report built on one is not a report.");
        }

        if (await rates.ExactAsync(from, to, on, cancellationToken) is { } already)
        {
            rates.Remove(already);
        }

        var recorded = ExchangeRate.Record(from, to, rate, on, source);

        rates.Add(recorded);
        await rates.SaveAsync(cancellationToken);

        return recorded;
    }

    public async Task ForgetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (await rates.RecentAsync(int.MaxValue, cancellationToken) is { } all
            && all.FirstOrDefault(one => one.Id == id) is { } found)
        {
            rates.Remove(found);
            await rates.SaveAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Convert an amount as at a date, saying which rate was used.
    /// </summary>
    /// <remarks>
    /// Returns the amount unchanged when it is already in the wanted currency, which is
    /// the common case and must not need a rate to have been recorded.
    ///
    /// Returns nothing rather than guessing when no rate exists on or before the date.
    /// A report that quietly used 1:1 because the table was empty would show a dollar
    /// invoice as a hundred and thirty shillings, and nothing on the screen would say so.
    /// </remarks>
    public async Task<Converted?> ConvertAsync(
        Domain.Common.Money amount,
        string into,
        DateOnly on,
        CancellationToken cancellationToken = default)
    {
        var wanted = into.Trim().ToUpperInvariant();

        if (amount.Currency == wanted)
        {
            return new Converted(amount, amount, 1m, on, null);
        }

        if (await rates.OnOrBeforeAsync(amount.Currency, wanted, on, cancellationToken)
            is not { } rate)
        {
            return null;
        }

        return new Converted(amount, rate.Apply(amount), rate.Rate, rate.On, rate.Source);
    }
}

/// <summary>
/// An amount, what it came to, and exactly how that was worked out.
/// </summary>
/// <remarks>
/// The rate and its date travel with the result on purpose. A converted figure with no
/// provenance is a number somebody has to take on trust, and the first question anybody
/// asks of one is "at what rate?".
/// </remarks>
public sealed record Converted(
    Domain.Common.Money From,
    Domain.Common.Money To,
    decimal Rate,
    DateOnly RateOn,
    string? Source)
{
    /// <summary>
    /// Was the rate from the day asked about, or an earlier one?
    /// </summary>
    /// <remarks>
    /// Shown on the screen when it was earlier, because a report converting September's
    /// invoices at August's rate is defensible and a report that did not say so is not.
    /// </remarks>
    public bool IsExactDate(DateOnly asked) => RateOn == asked;
}
