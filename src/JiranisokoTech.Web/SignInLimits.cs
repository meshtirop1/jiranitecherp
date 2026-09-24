using System.Threading.RateLimiting;

namespace JiranisokoTech.Web;

/// <summary>
/// The third form a stranger can post to, and the one that was left out.
/// </summary>
/// <remarks>
/// The careers form and the password-recovery form have each carried a rate limit since they
/// were written. Sign-in, the form that actually guards the accounts, carried none — and the
/// only throttle on it was Identity's lockout, which counts failures <b>per account</b>. That
/// is the wrong axis for the attack that matters. Somebody with a list of this firm's
/// addresses and the twenty commonest passwords never triggers a lockout: they try one
/// password against every address, then the next, and each account sees a single failure
/// between attempts hours apart. Password spraying is precisely the shape a per-account
/// lockout is blind to, and it is the shape a list of staff email addresses invites, which
/// the careers page publishes the domain of.
///
/// This is the flood bound and not the spray defence. It stops a machine hammering the form
/// before the request reaches a database at all, and it is deliberately loose enough that a
/// whole office behind one address never meets it. The spray defence is the durable one in
/// <c>SignInThrottle</c>, which counts failures from an address and survives a restart —
/// the same division as the recovery form, where the in-memory limit catches a flood and the
/// written-down one catches somebody moving between addresses to bury one person.
/// </remarks>
public static class SignInLimits
{
    public const string Limit = "sign-in";

    /// <summary>
    /// Sixty posts from one address in five minutes.
    /// </summary>
    /// <remarks>
    /// Chosen against the office rather than against the attacker, because the attacker is
    /// the other throttle's problem. Everybody here is behind one address, so the number has
    /// to survive a Monday morning: thirty people signing in, several mistyping, a few
    /// refreshing — and a limit that locked the firm out of its own application on the busiest
    /// ten minutes of the week would be turned off within a day, which is worse than not
    /// having one.
    ///
    /// Reading the page is never limited, for the reason the other two forms give: a page that
    /// refuses to render is indistinguishable from one that is broken, and somebody who
    /// cannot get in at seven on a Sunday is already having a bad time.
    /// </remarks>
    public static void AddSignInLimit(
        this Microsoft.AspNetCore.RateLimiting.RateLimiterOptions options) =>
        options.AddPolicy(Limit, context =>
            HttpMethods.IsPost(context.Request.Method)
                ? RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 60,
                        Window = TimeSpan.FromMinutes(5),
                        QueueLimit = 0,
                    })
                : RateLimitPartition.GetNoLimiter<string>("reading"));
}
