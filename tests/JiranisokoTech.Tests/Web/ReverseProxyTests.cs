using System.Net;
using JiranisokoTech.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// Whose address the application believes it is talking to.
/// </summary>
/// <remarks>
/// Two things read it: the sign-in trail, which records where an attempt came
/// from, and the careers rate limit, which partitions by it. Behind a reverse
/// proxy both see the proxy unless X-Forwarded-For is honoured — and honouring
/// it unconditionally is worse, because the header is client-supplied and a
/// stranger who can reach the application directly could then be any address
/// they liked. Trust has to be narrow and deliberate, so it is tested rather
/// than assumed.
///
/// Tested against a small host rather than the application, because the thing
/// under test is one middleware and its configuration; standing the whole
/// application up would prove the same thing more slowly and less clearly.
/// </remarks>
public class ReverseProxyTests
{
    private const string Proxy = "10.8.0.5";
    private const string RealClient = "41.90.64.17";

    [Fact]
    public async Task Without_a_trusted_network_a_forwarded_address_is_ignored()
    {
        await using var app = await HostAsync(trusted: null);

        var seen = await AskAsync(app, forwardedFor: RealClient);

        // The proxy, not the header. Refusing to believe an unauthenticated
        // header is the safe direction to be wrong in.
        Assert.Equal(Proxy, seen);
    }

    [Fact]
    public async Task A_forwarded_address_from_a_trusted_network_is_believed()
    {
        await using var app = await HostAsync(trusted: "10.8.0.0/24");

        var seen = await AskAsync(app, forwardedFor: RealClient);

        Assert.Equal(RealClient, seen);
    }

    /// <summary>
    /// The network has to be the one the request actually came from.
    /// </summary>
    /// <remarks>
    /// A range that does not contain the connecting address is not a weaker
    /// form of trust; it is no trust at all, and the header must be discarded
    /// exactly as if nothing had been configured.
    /// </remarks>
    [Fact]
    public async Task A_forwarded_address_from_somewhere_else_is_ignored()
    {
        await using var app = await HostAsync(trusted: "192.168.50.0/24");

        var seen = await AskAsync(app, forwardedFor: RealClient);

        Assert.Equal(Proxy, seen);
    }

    /// <summary>
    /// Loopback is not trusted merely because it is loopback.
    /// </summary>
    /// <remarks>
    /// It is in the framework's defaults, and those defaults are cleared. A
    /// proxy on the same host is a reasonable thing to trust and an unreasonable
    /// thing to trust silently.
    /// </remarks>
    [Fact]
    public async Task Loopback_is_not_trusted_by_default()
    {
        await using var app = await HostAsync(trusted: "10.8.0.0/24", from: "127.0.0.1");

        var seen = await AskAsync(app, forwardedFor: RealClient);

        Assert.Equal("127.0.0.1", seen);
    }

    /// <summary>
    /// A mistyped range stops the application rather than being skipped.
    /// </summary>
    /// <remarks>
    /// Skipping it would leave the application running with the safeguard
    /// silently absent, which is the failure this whole file is about. Somebody
    /// who has configured a proxy network has said what they mean; if it cannot
    /// be read, the right answer is to say so and not start.
    /// </remarks>
    [Fact]
    public async Task A_range_that_cannot_be_read_refuses_to_start()
    {
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => HostAsync(trusted: "10.8.0.0/not-a-prefix"));

        Assert.Contains("10.8.0.0/not-a-prefix", refusal.Message);
    }

    private static async Task<WebApplication> HostAsync(string? trusted, string from = Proxy)
    {
        var builder = WebApplication.CreateBuilder();

        builder.WebHost.UseTestServer();

        if (trusted is not null)
        {
            builder.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Proxy:TrustedNetworks:0"] = trusted });
        }

        var app = builder.Build();

        // Stands in for the connection the proxy would have made. The test
        // server has no real socket, so without this there is no remote address
        // for the middleware to judge.
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(from);
            await next();
        });

        app.UseReverseProxyHeaders();

        app.Run(context =>
            context.Response.WriteAsync(context.Connection.RemoteIpAddress?.ToString() ?? "none"));

        await app.StartAsync();

        return app;
    }

    private static async Task<string> AskAsync(WebApplication app, string forwardedFor)
    {
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Forwarded-For", forwardedFor);

        var response = await client.SendAsync(request);

        return await response.Content.ReadAsStringAsync();
    }
}
