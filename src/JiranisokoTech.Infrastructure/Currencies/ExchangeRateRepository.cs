using JiranisokoTech.Application.Currencies;
using JiranisokoTech.Domain.Money;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Currencies;

public sealed class ExchangeRateRepository(AppDbContext context) : IExchangeRateRepository
{
    /// <summary>
    /// The rate on a day, or the most recent one before it.
    /// </summary>
    /// <remarks>
    /// Ordered descending and taking one, which is the whole of the fallback. Never
    /// looks forward: converting March's invoices at September's rate is the error the
    /// dated rate exists to prevent, and a query with no upper bound would do exactly
    /// that whenever the month's rate had not been entered yet.
    /// </remarks>
    public Task<ExchangeRate?> OnOrBeforeAsync(
        string from,
        string to,
        DateOnly on,
        CancellationToken cancellationToken = default)
    {
        var source = from.Trim().ToUpperInvariant();
        var target = to.Trim().ToUpperInvariant();

        return context.ExchangeRates
            .AsNoTracking()
            .Where(rate => rate.From == source && rate.To == target && rate.On <= on)
            .OrderByDescending(rate => rate.On)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<List<ExchangeRate>> RecentAsync(
        int take = 100, CancellationToken cancellationToken = default) =>
        context.ExchangeRates
            .OrderByDescending(rate => rate.On)
            .ThenBy(rate => rate.From)
            .ThenBy(rate => rate.To)
            .Take(take)
            .ToListAsync(cancellationToken);

    public Task<ExchangeRate?> ExactAsync(
        string from,
        string to,
        DateOnly on,
        CancellationToken cancellationToken = default)
    {
        var source = from.Trim().ToUpperInvariant();
        var target = to.Trim().ToUpperInvariant();

        return context.ExchangeRates.FirstOrDefaultAsync(
            rate => rate.From == source && rate.To == target && rate.On == on,
            cancellationToken);
    }

    public void Add(ExchangeRate rate) => context.ExchangeRates.Add(rate);

    public void Remove(ExchangeRate rate) => context.ExchangeRates.Remove(rate);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        context.SaveChangesAsync(cancellationToken);
}
