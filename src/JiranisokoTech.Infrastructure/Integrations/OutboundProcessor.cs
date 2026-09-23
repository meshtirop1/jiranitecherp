using JiranisokoTech.Application.Integrations;
using JiranisokoTech.Domain.Integrations;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JiranisokoTech.Infrastructure.Integrations;

/// <summary>
/// Keeps the outbound dispatcher running for as long as the application does.
/// </summary>
/// <remarks>
/// The same shape as the outbox and webhook processors, for the same reasons. The one
/// difference is the poll interval: this one waits thirty seconds rather than five or
/// ten, because every pass that finds nothing is a query, and the notifications it
/// carries go to systems that are not waiting on a human being.
/// </remarks>
public sealed class OutboundProcessor(
    IServiceScopeFactory scopes,
    ILogger<OutboundProcessor> logger)
    : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan ErrorBackoff = TimeSpan.FromSeconds(30);

    private const int BatchSize = 25;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Outgoing webhook dispatcher started: {BatchSize} per pass, every {Interval}.",
            BatchSize,
            PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = PollInterval;

            try
            {
                using var scope = scopes.CreateScope();

                var dispatcher = scope.ServiceProvider.GetRequiredService<OutboundDispatcher>();

                if (await dispatcher.RunOnceAsync(BatchSize, stoppingToken) >= BatchSize)
                {
                    // A full batch means there is probably more, so a backlog drains
                    // at the speed of the endpoints rather than of this clock.
                    delay = TimeSpan.Zero;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception, "An outgoing webhook pass failed. It will be tried again.");

                delay = ErrorBackoff;
            }

            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        logger.LogInformation("Outgoing webhook dispatcher stopped.");
    }
}

/// <summary>The reads the outgoing webhook screen does.</summary>
public sealed class IntegrationQueries(AppDbContext database)
{
    public async Task<List<SubscriptionRow>> SubscriptionsAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await database.Subscriptions
            .AsNoTracking()
            .OrderBy(one => one.DisabledAt == null ? 0 : 1)
            .ThenBy(one => one.Name)
            .Select(one => new
            {
                one.Id,
                one.Name,
                one.Endpoint,
                one.CreatedAt,
                one.LastDeliveryAt,
                one.DisabledAt,
                one.DisabledReason,
                one.ConsecutiveFailures,
                Events = one.Wanted.Select(wanted => wanted.Name).ToList(),
                Waiting = database.OutboundDeliveries.Count(delivery =>
                    delivery.SubscriptionId == one.Id
                    && (delivery.Status == OutboundStatus.Waiting
                        || delivery.Status == OutboundStatus.Failed)),
                Failing = database.OutboundDeliveries.Count(delivery =>
                    delivery.SubscriptionId == one.Id
                    && delivery.Status == OutboundStatus.DeadLettered),
            })
            .ToListAsync(cancellationToken);

        return [.. rows.Select(row => new SubscriptionRow(
            row.Id,
            row.Name,
            row.Endpoint,
            row.Events,
            row.CreatedAt,
            row.LastDeliveryAt,
            row.DisabledAt is null,
            row.DisabledReason,
            row.ConsecutiveFailures,
            row.Waiting,
            row.Failing))];
    }

    public async Task<List<OutboundRow>> DeliveriesAsync(
        Guid? subscriptionId = null,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        var query = database.OutboundDeliveries.AsNoTracking();

        if (subscriptionId is { } subscription)
        {
            query = query.Where(one => one.SubscriptionId == subscription);
        }

        var rows = await query
            .OrderByDescending(one => one.QueuedAt)
            .Take(take)
            .Select(one => new
            {
                one.Id,
                one.Event,
                one.Status,
                one.Attempts,
                one.QueuedAt,
                one.SentAt,
                one.ResponseCode,
                one.Error,
                Subscription = database.Subscriptions
                    .Where(subscription => subscription.Id == one.SubscriptionId)
                    .Select(subscription => subscription.Name)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        return [.. rows.Select(row => new OutboundRow(
            row.Id,
            row.Subscription,
            row.Event,
            row.Status,
            row.Attempts,
            row.QueuedAt,
            row.SentAt,
            row.ResponseCode,
            row.Error))];
    }

    /// <summary>How many notifications have given up, for the warning at the top.</summary>
    public Task<int> FailingAsync(CancellationToken cancellationToken = default) =>
        database.OutboundDeliveries.CountAsync(
            one => one.Status == OutboundStatus.DeadLettered, cancellationToken);
}

public sealed record SubscriptionRow(
    Guid Id,
    string Name,
    string Endpoint,
    IReadOnlyList<string> Events,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastDeliveryAt,
    bool IsActive,
    string? DisabledReason,
    int ConsecutiveFailures,
    int Waiting,
    int Failing);

public sealed record OutboundRow(
    Guid Id,
    string? Subscription,
    string Event,
    OutboundStatus Status,
    int Attempts,
    DateTimeOffset QueuedAt,
    DateTimeOffset? SentAt,
    int? ResponseCode,
    string? Error);
