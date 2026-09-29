using System.Diagnostics;
using JiranisokoTech.Application.Observability;
using Microsoft.Extensions.Hosting;

namespace JiranisokoTech.Infrastructure.Observability;

/// <summary>
/// Makes the application's own spans real, so that background work has a trace id.
/// </summary>
/// <remarks>
/// <b>Without a listener, a span is never created.</b> <c>ActivitySource.StartActivity</c>
/// returns null when nothing is listening, so every <c>using var span = …</c> in the scheduler
/// and the dispatchers did nothing: the work ran with no activity, and nothing it logged could
/// be tied to anything else it logged. A request always had a trace id, because ASP.NET makes
/// its own; the outbox, the webhook processor and the jobs — the work nobody is waiting on,
/// which is the work that fails unnoticed — had none.
///
/// This listens to the application's source only, and records everything. It exports nothing:
/// the trace id reaches the JSON logs through the logging's activity tracking, which is where a
/// firm with one server looks. An OpenTelemetry exporter can be added later and will find the
/// same spans without anything here changing.
/// </remarks>
public sealed class TraceListener : IHostedService, IDisposable
{
    private ActivityListener? _listener;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Telemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
        };

        ActivitySource.AddActivityListener(_listener);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _listener?.Dispose();
        _listener = null;
    }
}
