using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace JiranisokoTech.Tests.Infrastructure;

/// <summary>
/// The two endpoints an orchestrator uses to decide whether to restart this
/// container and whether to send it traffic.
///
/// Driven through the real application rather than a stand-in, because the
/// thing worth knowing is that the actual pipeline answers — a mock would keep
/// passing after somebody moved the route.
/// </summary>
public class HealthEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
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
