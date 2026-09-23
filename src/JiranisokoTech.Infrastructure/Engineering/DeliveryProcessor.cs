using JiranisokoTech.Application.Engineering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JiranisokoTech.Infrastructure.Engineering;

/// <summary>
/// Keeps the delivery dispatcher running for as long as the application does.
/// </summary>
/// <remarks>
/// The same shape as <see cref="Messaging.OutboxProcessor"/> and for the same
/// reasons: only the loop is here, so that a test can run one pass without
/// starting a service and waiting; a full batch goes straight round again, so a
/// backlog drains at the speed of the handlers rather than the speed of the
/// poll; and a pass that throws does not kill the service, because the usual
/// cause is a database that is briefly unreachable and a processor that exits on
/// the first blip leaves a container that is up, healthy, and quietly recording
/// nothing.
/// </remarks>
public sealed class DeliveryProcessor(
    IServiceScopeFactory scopes,
    IOptions<GitOptions> options,
    ILogger<DeliveryProcessor> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;

        logger.LogInformation(
            "Webhook delivery dispatcher started: {BatchSize} per pass, every {Interval}.",
            settings.BatchSize,
            settings.PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = settings.PollInterval;

            try
            {
                // A scope per pass: the dispatcher holds a DbContext, and one
                // held for the lifetime of the process would accumulate every
                // entity it had ever tracked.
                using var scope = scopes.CreateScope();

                var dispatcher = scope.ServiceProvider
                    .GetRequiredService<DeliveryDispatcher>();

                var handled = await dispatcher.RunOnceAsync(settings.BatchSize, stoppingToken);

                if (handled >= settings.BatchSize)
                {
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
                    exception, "A webhook dispatch pass failed. It will be tried again.");

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

        logger.LogInformation("Webhook delivery dispatcher stopped.");
    }
}
