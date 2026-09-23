using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Web.Identity;

/// <summary>
/// Where an account has been used, and which of those is new.
/// </summary>
/// <remarks>
/// Section 4 had a working "sign out everywhere" and no way to see what that would
/// end — so the button answered a question nobody could ask. This is the question:
/// which browsers and which addresses have signed into this account, when did each
/// first appear, and when was each last used.
///
/// <b>It is not a device registry, and it is called what it is.</b> A real one needs a
/// durable identifier set on the browser and a server-side record of every live
/// session, which cookie authentication does not give. What this reads is the sign-in
/// trail, so it can say "this browser at this address has signed in eleven times since
/// August" and it cannot say "end that one and leave the others". Ending one session
/// individually is not offered, because the only honest button is the one already there:
/// end all of them.
///
/// Saying so on the screen matters more than the feature. Somebody shown a list headed
/// "your devices" reasonably assumes each row can be revoked, and discovers otherwise
/// at the worst possible moment.
/// </remarks>
public sealed class SignInPlaces(AppDbContext database)
{
    /// <summary>
    /// How many sign-ins make a place familiar.
    /// </summary>
    /// <remarks>
    /// One. A place is new the first time and known ever after, because the question
    /// being answered is "have I been here before" and the answer to that is not a
    /// threshold.
    /// </remarks>
    private const int Once = 1;

    public async Task<List<SignInPlace>> ForAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var records = await database.Set<SignInRecord>()
            .AsNoTracking()
            .Where(record => record.UserId == userId
                && record.Outcome == SignInOutcome.Succeeded)
            .Select(record => new { record.IpAddress, record.UserAgent, record.At })
            .ToListAsync(cancellationToken);

        /*
         * Grouped on the address and the browser together. Either alone is misleading:
         * one browser moves between an office and a home connection, and one address
         * covers everybody in the office. The pair is roughly "this person on this
         * machine", which is as close as a trail of headers gets.
         */
        return
        [
            .. records
                .GroupBy(record => new
                {
                    Address = record.IpAddress ?? "unknown",
                    Browser = Describe(record.UserAgent),
                })
                .Select(group => new SignInPlace(
                    group.Key.Address,
                    group.Key.Browser,
                    group.Min(record => record.At),
                    group.Max(record => record.At),
                    group.Count()))
                .OrderByDescending(place => place.LastAt),
        ];
    }

    /// <summary>
    /// Is this sign-in from somewhere the account has not been used before?
    /// </summary>
    /// <remarks>
    /// The whole of the suspicious-sign-in detection, and it is deliberately this small.
    ///
    /// What it does not attempt: geography, because that needs an address database this
    /// system would then have to keep current, and a wrong country on a security warning
    /// destroys the warning's credibility faster than no warning at all. Nor impossible
    /// travel, for the same reason.
    ///
    /// What it does is the thing that actually catches a stolen password: the first
    /// successful sign-in from a browser and address this account has never used. That
    /// is exactly what an attacker produces, and it is also what a new laptop produces —
    /// so it is a notice to the person, never a block. Blocking on it would lock people
    /// out of their own accounts on the day they buy a machine.
    ///
    /// The first sign-in to a brand-new account is not unfamiliar. Everything is
    /// unfamiliar then, and warning somebody about the sign-in they are performing
    /// teaches them to ignore the warning.
    /// </remarks>
    public async Task<bool> IsSomewhereNewAsync(
        Guid userId,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        var address = ipAddress ?? "unknown";
        var browser = Describe(userAgent);

        var previous = await database.Set<SignInRecord>()
            .AsNoTracking()
            .Where(record => record.UserId == userId
                && record.Outcome == SignInOutcome.Succeeded)
            .Select(record => new { record.IpAddress, record.UserAgent })
            .ToListAsync(cancellationToken);

        if (previous.Count <= Once)
        {
            // Nothing to be unfamiliar against. See the remarks.
            return false;
        }

        return !previous.Any(record =>
            (record.IpAddress ?? "unknown") == address
            && Describe(record.UserAgent) == browser);
    }

    /// <summary>
    /// A user agent, reduced to something a person recognises.
    /// </summary>
    /// <remarks>
    /// A user agent string is a paragraph of version numbers that changes every few
    /// weeks, so grouping on it raw would make a new row every time a browser updated —
    /// and a list of forty rows for one laptop answers nothing. Reduced to the browser
    /// and the platform, which is what somebody actually checks against.
    ///
    /// The order matters: Edge claims to be Chrome and Chrome claims to be Safari, so
    /// each has to be ruled out before the one it impersonates.
    /// </remarks>
    public static string Describe(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return "An unidentified browser";
        }

        var browser = userAgent switch
        {
            var agent when agent.Contains("Edg/", StringComparison.OrdinalIgnoreCase) => "Edge",
            var agent when agent.Contains("OPR/", StringComparison.OrdinalIgnoreCase) => "Opera",
            var agent when agent.Contains("Firefox/", StringComparison.OrdinalIgnoreCase)
                => "Firefox",
            var agent when agent.Contains("Chrome/", StringComparison.OrdinalIgnoreCase)
                => "Chrome",
            var agent when agent.Contains("Safari/", StringComparison.OrdinalIgnoreCase)
                => "Safari",
            _ => "Something else",
        };

        var platform = userAgent switch
        {
            var agent when agent.Contains("Android", StringComparison.OrdinalIgnoreCase)
                => "Android",
            var agent when agent.Contains("iPhone", StringComparison.OrdinalIgnoreCase)
                => "iPhone",
            var agent when agent.Contains("iPad", StringComparison.OrdinalIgnoreCase) => "iPad",
            var agent when agent.Contains("Windows", StringComparison.OrdinalIgnoreCase)
                => "Windows",
            var agent when agent.Contains("Mac OS", StringComparison.OrdinalIgnoreCase) => "a Mac",
            var agent when agent.Contains("Linux", StringComparison.OrdinalIgnoreCase) => "Linux",
            _ => "an unknown system",
        };

        return $"{browser} on {platform}";
    }
}

/// <summary>
/// One browser, at one address, that has signed into an account.
/// </summary>
/// <remarks>
/// Not a session and not a device. See SignInPlaces for what this can and cannot say.
/// </remarks>
public sealed record SignInPlace(
    string Address,
    string Browser,
    DateTimeOffset FirstAt,
    DateTimeOffset LastAt,
    int Times)
{
    /// <summary>Has this account only ever been used here once?</summary>
    /// <remarks>
    /// Worth showing, because a place used once and never again is either a machine
    /// somebody borrowed or the one an attacker used, and both are worth a second look.
    /// </remarks>
    public bool Once => Times == 1;
}
