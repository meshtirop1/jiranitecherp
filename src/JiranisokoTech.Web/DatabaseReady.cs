using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace JiranisokoTech.Web;

/// <summary>
/// Whether the database answers, for <c>/ready</c> and never for <c>/health</c>.
/// </summary>
/// <remarks>
/// <b>This exists because <c>/ready</c> checked nothing.</b> The comment above its registration
/// said readiness checks dependencies, and so did the test of it, but the only check registered
/// was one that always answers Healthy — so an instance whose database had gone went on
/// receiving traffic and turning every request into the error page, which is precisely the
/// case readiness exists to take out of the load balancer.
///
/// The cache is deliberately not checked. Every Redis call is inside a broad catch that falls
/// through to the database, so an instance without its cache is slow rather than broken, and
/// taking it out of service for that would turn a cache outage into an application outage.
///
/// A fault is caught and reported as Unhealthy rather than left to throw. A provider that
/// raises rather than answering false would otherwise surface as the health middleware's own
/// failure, and the orchestrator would be told something went wrong with the check instead of
/// being told the database is not there.
/// </remarks>
public sealed class DatabaseReady(AppDbContext database) : IHealthCheck
{
    public const string Name = "database";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await database.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("The database is not answering.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("The database is not answering.", exception);
        }
    }
}
