using System.Threading.RateLimiting;

namespace JiranisokoTech.Web;

/// <summary>
/// The one form in this system a stranger can post to.
/// </summary>
/// <remarks>
/// Everything else is behind a sign-in, so everything else is rate-limited by
/// the fact that somebody had to get an account first. This is not, and an
/// application form that anybody on the internet can submit in a loop is a
/// database full of rubbish and a disk full of files by the morning.
/// </remarks>
public static class CareersLimits
{
    public const string Limit = "careers";

    /// <summary>
    /// Five applications from one address in ten minutes.
    /// </summary>
    /// <remarks>
    /// Generous for a person — nobody applies to five jobs in ten minutes by
    /// accident, and if they do they can wait — and useless to anybody running
    /// a script.
    ///
    /// Partitioned by remote address, which is the only thing available before
    /// anybody signs in. It is imperfect: an office behind one address shares a
    /// bucket. That is the trade, and the numbers are set high enough that a
    /// shared address is unlikely to notice.
    /// </remarks>
    public static void AddCareersLimit(this Microsoft.AspNetCore.RateLimiting.RateLimiterOptions options)
    {
        options.AddPolicy(Limit, context =>
            /*
             * Submissions are limited; reading is not.
             *
             * The first version limited the whole page, which counted somebody
             * browsing the openings against the same five-per-ten-minutes as
             * somebody submitting — so a visitor reading six adverts was told
             * to come back later. An advert nobody can read is worse than no
             * advert.
             */
            HttpMethods.IsPost(context.Request.Method)
                ? RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(10),
                        QueueLimit = 0,
                    })
                : RateLimitPartition.GetNoLimiter<string>("reading"));

        // 429, not a redirect. A client that is being told to slow down needs a
        // status it can act on, and a person sees the page below.
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    }
}
