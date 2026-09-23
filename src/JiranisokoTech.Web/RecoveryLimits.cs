using System.Threading.RateLimiting;

namespace JiranisokoTech.Web;

/// <summary>
/// The second form in this system a stranger can post to, and the more dangerous of the two.
/// </summary>
/// <remarks>
/// The careers form fills a table with rubbish when somebody runs it in a loop. This one
/// sends mail from the firm's own domain to any address typed into it, which makes a loop
/// somebody else's problem as well as the firm's: an inbox full of password letters from
/// jiranisokotech.co.ke is how a domain gets a reputation it takes months to lose.
///
/// This is only half the guard, and the weaker half. It partitions by the address the request
/// came from, which is what catches a flood from one machine and does nothing at all about
/// somebody moving between addresses to bury one person. The durable per-recipient throttle in
/// UserAdministration is the half that catches that, and it is the one that survives a
/// restart.
/// </remarks>
public static class RecoveryLimits
{
    public const string Limit = "recovery";

    /// <summary>Five asks from one address in fifteen minutes.</summary>
    /// <remarks>
    /// Generous for a person who has mistyped their address twice and is getting cross, and
    /// useless to a script. Reading the page is never limited, for the same reason the
    /// careers page is not: somebody locked out at seven on a Sunday refreshing the form is
    /// not an attack, and a page that refuses to render is indistinguishable from one that is
    /// broken.
    /// </remarks>
    public static void AddRecoveryLimit(
        this Microsoft.AspNetCore.RateLimiting.RateLimiterOptions options) =>
        options.AddPolicy(Limit, context =>
            HttpMethods.IsPost(context.Request.Method)
                ? RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(15),
                        QueueLimit = 0,
                    })
                : RateLimitPartition.GetNoLimiter<string>("reading"));
}
