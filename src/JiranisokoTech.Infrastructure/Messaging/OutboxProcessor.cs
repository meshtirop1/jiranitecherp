using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JiranisokoTech.Infrastructure.Messaging;

/// <summary>
/// Keeps the dispatcher running for as long as the application does.
/// </summary>
/// <remarks>
/// Only the loop lives here. The work is in <see cref="OutboxDispatcher"/>, so
/// that it can be run once, on demand, by a test — a background service that
/// contains its own logic can only be tested by starting it and waiting, which
/// produces suites that are slow when they pass and mystifying when they fail.
///
/// Two rules in the loop are worth naming:
///
/// A full batch means there is probably more, so the next pass starts at once.
/// A backlog then drains at the speed of the handlers rather than the speed of
/// the poll interval, which matters after an outage when several thousand rows
/// are waiting.
///
/// A pass that throws does not kill the service. The usual cause is the database
/// being briefly unreachable, and a dispatcher that exits on the first blip
/// leaves a container that is up, healthy, and silently delivering nothing.
/// </remarks>
public sealed class OutboxProcessor(
    IServiceScopeFactory scopes,
    IOptions<OutboxOptions> options,
    ILogger<OutboxProcessor> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;

        logger.LogInformation(
            "Outbox dispatcher started: {BatchSize} per pass, every {Interval}.",
            settings.BatchSize,
            settings.PollInterval);

        // Started rather than zeroed, so the first sweep waits out one interval
        // instead of running during startup, when the process has better things
        // to be doing.
        var sincePrune = Stopwatch.StartNew();

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = settings.PollInterval;

            try
            {
                // A scope per pass: the dispatcher holds a DbContext, and one
                // held for the lifetime of the process would accumulate every
                // entity it had ever tracked.
                using var scope = scopes.CreateScope();

                var dispatcher = scope.ServiceProvider.GetRequiredService<OutboxDispatcher>();
                var settled = await dispatcher.RunOnceAsync(stoppingToken);

                if (settled >= settings.BatchSize)
                {
                    delay = TimeSpan.Zero;
                }

                if (sincePrune.Elapsed >= settings.PruneInterval)
                {
                    await dispatcher.PruneAsync(stoppingToken);
                    sincePrune.Restart();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "An outbox pass failed. Backing off.");

                delay = settings.ErrorBackoff;
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

        logger.LogInformation("Outbox dispatcher stopped.");
    }
}
