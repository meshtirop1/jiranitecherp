using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Web.Identity;

/// <summary>
/// How many failures one address is allowed before it is asked to wait.
/// </summary>
/// <remarks>
/// The half that catches a password spray, and the half that survives a restart.
///
/// Identity's lockout counts failures per account, which is the wrong axis for the attack
/// this firm is actually exposed to: one password tried against every address in turn never
/// gives any single account enough failures to lock. Counting per <b>address</b> catches
/// exactly that, and it is the count nothing in this system was keeping.
///
/// The evidence was already being written. <c>SignInRecord</c> has recorded every attempt,
/// successful or not, since it was added, with the address it came from — and its own remark
/// says the failures are "the more useful half" because "a burst of failures from one address
/// is the shape of an attack". Nothing read them. This reads them.
/// </remarks>
public sealed class SignInThrottle(AppDbContext database, IClock clock)
{
    /// <summary>
    /// How many failures from one address inside the window before it must wait.
    /// </summary>
    /// <remarks>
    /// Fifteen, which is a great many for a person and useless to a script. Somebody who has
    /// forgotten which of their two passwords this is, tried both, tried them with caps lock
    /// on and then asked a colleague is nowhere near it; a list of addresses tried against one
    /// password stops after fifteen, and the whole point of a spray is the thousands after
    /// that.
    ///
    /// Deliberately not derived from the lockout's own threshold. They count different things
    /// — that one counts a person getting their own password wrong, this one counts an address
    /// getting other people's wrong — and tying them together would mean tuning one for the
    /// other's reason.
    /// </remarks>
    public const int MostFailures = 15;

    /// <summary>The window those failures are counted over.</summary>
    /// <remarks>
    /// A quarter of an hour, matching the lockout the page already tells people about, so the
    /// sentence on the screen is true of both: waiting is the fix, and this is how long.
    /// </remarks>
    public static TimeSpan Window { get; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Has this address already failed too often to be allowed another go?
    /// </summary>
    /// <remarks>
    /// A null address is never throttled, and that is a decision rather than an oversight. It
    /// means the connection had no remote address this process could see — which happens
    /// behind a proxy that has not been configured to forward it, and in the tests — and
    /// throttling every such request as one caller would take the whole firm down the first
    /// time somebody changed a reverse proxy. The in-memory limiter partitions those together
    /// under "unknown" and bounds the volume; that is the right place for a guess.
    /// </remarks>
    public async Task<bool> TooManyFailuresAsync(
        string? ipAddress, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            return false;
        }

        var since = clock.Now - Window;

        /*
         * Counted rather than read, and capped by the count itself: the query stops mattering
         * once it passes the threshold, and an attacker cannot make this read grow.
         *
         * SecondFactorRequired is not a failure. The password was right and the person is half
         * way in, so counting it would throttle exactly the accounts that took the trouble to
         * turn two-step on.
         */
        var failures = await database.Set<SignInRecord>()
            .AsNoTracking()
            .CountAsync(
                record => record.IpAddress == ipAddress
                    && record.At >= since
                    && (record.Outcome == SignInOutcome.Refused
                        || record.Outcome == SignInOutcome.LockedOut
                        || record.Outcome == SignInOutcome.Deactivated),
                cancellationToken);

        return failures >= MostFailures;
    }
}
