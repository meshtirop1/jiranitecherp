using System.Net;
using JiranisokoTech.Tests.Identity;
using JiranisokoTech.Web;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace JiranisokoTech.Tests.Infrastructure;

/// <summary>
/// The two endpoints an orchestrator uses to decide whether to restart this
/// container and whether to send it traffic.
///
/// Driven through the real application rather than a stand-in, because the
/// thing worth knowing is that the actual pipeline answers — a mock would keep
/// passing after somebody moved the route.
///
/// ApplicationFactory rather than a plain WebApplicationFactory, and that is
/// not incidental. The plain one takes the connection string from
/// appsettings.json, which points at a SQLite file on the developer's disk —
/// so these tests ran against a database somebody had been using by hand, and
/// whose schema is only ever created when it does not already exist. They
/// passed for weeks and then failed the day a new table was added, reporting a
/// missing table rather than anything to do with health endpoints.
/// </summary>
public class HealthEndpointTests(ApplicationFactory factory)
    : IClassFixture<ApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();


    [Fact]
    public async Task Health_says_the_process_is_alive()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ready_says_the_instance_can_serve_traffic()
    {
        var response = await _client.GetAsync("/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Liveness must not depend on anything outside the process. If it did, a
    /// database outage would restart every instance in a loop and turn a
    /// recoverable incident into an outage — so /health is deliberately thinner
    /// than /ready, and this is the test that keeps it that way.
    /// </summary>
    [Fact]
    public async Task Liveness_checks_less_than_readiness()
    {
        var live = await _client.GetAsync("/health");
        var ready = await _client.GetAsync("/ready");

        Assert.True(live.IsSuccessStatusCode);
        Assert.True(ready.IsSuccessStatusCode);

        // Both are healthy today. The distinction that matters is which checks
        // each one runs, asserted where the checks are registered rather than
        // by breaking a dependency here.
        Assert.Equal("Healthy", await live.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Readiness asks the database, and liveness does not.
    /// </summary>
    /// <remarks>
    /// Written because readiness asked nothing: the only check registered was one that always
    /// answers Healthy, while the comment above it and the test before this one both said it
    /// checked dependencies. Asserted on the registrations, because the split is the point —
    /// the database check on /health would restart every instance in a loop during an outage,
    /// and its absence from /ready sends traffic to an instance that cannot serve it.
    /// </remarks>
    [Fact]
    public void Readiness_checks_the_database_and_liveness_does_not()
    {
        var checks = factory.Services
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations;

        var database = Assert.Single(checks, check => check.Name == DatabaseReady.Name);

        Assert.DoesNotContain("live", database.Tags);
    }

    /// <summary>
    /// A database that cannot be reached makes the instance not ready.
    /// </summary>
    /// <remarks>
    /// A SQLite file under a directory that does not exist, opened read-only so that nothing
    /// creates it: the nearest thing in a test to a database server that has gone.
    /// </remarks>
    [Fact]
    public async Task A_database_that_cannot_be_reached_is_not_ready()
    {
        var gone = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "gone.db");

        using var scope = factory.Services.CreateScope();
        var clock = scope.ServiceProvider
            .GetRequiredService<JiranisokoTech.Application.Abstractions.IClock>();
        var user = scope.ServiceProvider
            .GetRequiredService<JiranisokoTech.Application.Abstractions.ICurrentUser>();

        await using var unreachable = new JiranisokoTech.Infrastructure.Persistence.AppDbContext(
            new DbContextOptionsBuilder<JiranisokoTech.Infrastructure.Persistence.AppDbContext>()
                .UseSqlite($"Data Source={gone};Mode=ReadOnly")
                .Options,
            clock,
            user);

        var result = await new DatabaseReady(unreachable)
            .CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    /// <summary>
    /// Everything that is not a health probe is closed to a stranger.
    ///
    /// Deny by default, so a page added without an attribute is protected
    /// rather than public. The opposite default fails silently: nobody
    /// discovers the omission until the page is one that mattered.
    /// </summary>
    [Fact]
    public async Task An_unauthenticated_visitor_is_sent_to_sign_in()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("/sign-in", response.Headers.Location!.OriginalString);
    }
}
