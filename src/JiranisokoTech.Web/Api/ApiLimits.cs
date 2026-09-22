using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace JiranisokoTech.Web.Api;

/// <summary>
/// How often one key may call.
/// </summary>
/// <remarks>
/// Partitioned by the key, not by address. An integration runs from one server
/// and every call it makes shares an address, so limiting by address would mean
/// one caller's nightly job throttling everybody else's — and would do nothing
/// about a caller spread across several machines.
///
/// A request with no key at all falls to a single shared partition. It is going
/// to be refused by authorization in any case; the limit is there so that
/// refusing it a thousand times a second is also cheap.
/// </remarks>
public static class ApiLimits
{
    public const string Policy = "api";

    public static RateLimiterOptions AddApiLimits(this RateLimiterOptions limiter)
    {
        limiter.AddPolicy(Policy, context =>
        {
            var key = context.User.FindFirst("key")?.Value;

            if (key is null)
            {
                return RateLimitPartition.GetFixedWindowLimiter(
                    "anonymous",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 30,
                        Window = TimeSpan.FromMinutes(1),
                    });
            }

            return RateLimitPartition.GetTokenBucketLimiter(
                key,
                _ => new TokenBucketRateLimiterOptions
                {
                    // A bucket rather than a fixed window, so a caller that
                    // sits idle and then fetches twenty pages in a burst is not
                    // punished for the shape of its work, while a caller that
                    // hammers continuously still settles to the refill rate.
                    TokenLimit = 120,
                    TokensPerPeriod = 60,
                    ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
        });

        return limiter;
    }
}
