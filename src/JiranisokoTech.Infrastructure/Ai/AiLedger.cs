using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace JiranisokoTech.Infrastructure.Ai;

/// <summary>
/// The usage log, and the daily limit counted from it.
/// </summary>
/// <remarks>
/// The limit is counted from the log rather than kept in a counter, so the two cannot disagree:
/// a counter that was incremented and then not saved, or saved and then not incremented, would be
/// a limit that drifted from what the firm was actually billed for.
/// </remarks>
public sealed class AiLedger(AppDbContext database, IClock clock, IOptions<AiOptions> options)
{
    public int DailyLimit => options.Value.DailyLimit;

    /// <summary>How many uses this account has left today.</summary>
    /// <remarks>
    /// "Today" is the UTC day. The firm is in Nairobi, three hours ahead, so the allowance renews
    /// at three in the morning local time — which nobody will be awake to notice, and which avoids
    /// a limit that depends on the server's time zone.
    /// </remarks>
    public async Task<int> RemainingTodayAsync(Guid? accountId, CancellationToken cancellationToken = default)
    {
        var since = new DateTimeOffset(clock.Today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var used = await database.AiExchanges
            .AsNoTracking()
            .CountAsync(one => one.AccountId == accountId && one.At >= since, cancellationToken);

        return Math.Max(0, DailyLimit - used);
    }

    public async Task RecordAsync(AiExchange exchange, CancellationToken cancellationToken = default)
    {
        database.AiExchanges.Add(exchange);
        await database.SaveChangesAsync(cancellationToken);
    }

    public Task<List<AiExchange>> RecentAsync(int take = 200, CancellationToken cancellationToken = default) =>
        database.AiExchanges
            .AsNoTracking()
            .OrderByDescending(one => one.At)
            .Take(take)
            .ToListAsync(cancellationToken);
}
