using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;

namespace JiranisokoTech.Web;

/// <summary>
/// Reading the client's real address from behind a reverse proxy.
/// </summary>
/// <remarks>
/// The container speaks plain HTTP and something in front of it terminates TLS.
/// That proxy is the only thing this application ever sees connecting to it, so
/// without the headers below two things are quietly wrong:
///
/// The sign-in trail records an address for every attempt, and the point of
/// recording it is to notice a run of failures from somewhere unexpected. Every
/// row saying the same thing — the proxy — is not a weaker version of that; it
/// is nothing at all.
///
/// The careers page rate limit partitions by address. Sharing one partition
/// across every visitor means one person submitting applications throttles the
/// entire internet, and the limit stops doing the job it was added for.
///
/// Cookies are unaffected: they are marked Secure unconditionally rather than
/// by looking at the request's scheme, which is the safer way round.
/// </remarks>
public static class ReverseProxy
{
    /// <summary>
    /// Trust <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c>, but only from
    /// networks that have been named.
    /// </summary>
    /// <remarks>
    /// The important part is what this does <em>not</em> do. The usual advice
    /// for running behind a proxy in a container is to clear KnownNetworks and
    /// KnownProxies, because the proxy's address is not known ahead of time.
    /// That makes the headers unconditionally trusted, and they are trivially
    /// forged: anybody who can reach the application directly can then claim any
    /// address they like, which defeats the rate limit and writes fiction into
    /// the sign-in trail. It converts two safeguards into two lies, which is
    /// worse than not having them.
    ///
    /// So the networks are configuration, and if none are configured the
    /// middleware is not added at all — the application reads the address it
    /// can actually see. That is wrong behind a proxy, but wrong in the
    /// direction that under-counts rather than the one that can be steered by a
    /// stranger, and it is stated in the log at startup rather than assumed.
    /// </remarks>
    public static void UseReverseProxyHeaders(this WebApplication app)
    {
        var configured = app.Configuration
            .GetSection("Proxy:TrustedNetworks")
            .Get<string[]>() ?? [];

        var networks = new List<IPNetwork>();

        foreach (var entry in configured.Where(one => !string.IsNullOrWhiteSpace(one)))
        {
            if (Parse(entry) is { } network)
            {
                networks.Add(network);
            }
            else
            {
                // Loudly. A mistyped CIDR that is silently skipped leaves the
                // application behaving as though no proxy were configured,
                // which is the failure this whole file exists to prevent.
                throw new InvalidOperationException(
                    $"Proxy:TrustedNetworks contains \"{entry}\", which is not an address range "
                    + "in the form 10.0.0.0/8. Correct it or remove it.");
            }
        }

        if (networks.Count == 0)
        {
            app.Logger.LogInformation(
                "No trusted proxy networks are configured, so client addresses are read from the "
                + "connection. Behind a reverse proxy this records the proxy for every request; "
                + "set Proxy__TrustedNetworks__0 to the network it connects from.");

            return;
        }

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,

            // One hop. The proxy appends the address it saw; anything further
            // left in the header was put there by the client and is not
            // evidence of anything.
            ForwardLimit = 1,
        };

        // KnownIPNetworks, not KnownNetworks. Both properties exist and they
        // are two views of the same list; the older one takes the ASP.NET type
        // called IPNetwork and is deprecated in favour of the one in System.Net.
        //
        // Cleared rather than added to. The defaults trust loopback, which is
        // the right default for a proxy on the same machine and the wrong one
        // here — the point of this method is that trust is stated, not assumed.
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();

        foreach (var network in networks)
        {
            options.KnownIPNetworks.Add(network);
        }

        app.UseForwardedHeaders(options);

        app.Logger.LogInformation(
            "Trusting forwarded headers from {Count} network(s): {Networks}.",
            networks.Count,
            string.Join(", ", configured));
    }

    private static IPNetwork? Parse(string entry)
    {
        var parts = entry.Split('/', 2);

        if (parts.Length != 2
            || !IPAddress.TryParse(parts[0], out var address)
            || !int.TryParse(parts[1], out var prefix))
        {
            return null;
        }

        var widest = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;

        return prefix < 0 || prefix > widest ? null : new IPNetwork(address, prefix);
    }
}
