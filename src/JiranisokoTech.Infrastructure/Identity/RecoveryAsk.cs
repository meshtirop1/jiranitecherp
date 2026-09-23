namespace JiranisokoTech.Infrastructure.Identity;

/// <summary>
/// What happened when somebody asked for a way back into an account.
/// </summary>
/// <remarks>
/// <see cref="NoSuchAccount"/> is recorded rather than discarded, and it is the one worth
/// having. Somebody working through a list of addresses leaves a trail of these, and it is
/// the only evidence the list ever existed — the page itself says the same sentence either
/// way, deliberately, so nothing on screen distinguishes them.
/// </remarks>
public enum RecoveryOutcome
{
    /// <summary>A link was made and sent.</summary>
    LinkSent = 1,

    /// <summary>The address matched no account, and nothing was sent.</summary>
    NoSuchAccount = 2,

    /// <summary>Too many asks about this address, too close together.</summary>
    Throttled = 3,
}

/// <summary>
/// One request for a password link.
/// </summary>
/// <remarks>
/// Section 4 had no forgotten-password page at all: an administrator had to issue a link by
/// hand, which is fine on the afternoon the system is installed and useless at seven on a
/// Sunday evening. The account holder is the one person who cannot ask for help through the
/// system, because they cannot get into it.
///
/// This table exists for the throttle rather than for the history, and that is the whole
/// reason it is durable rather than in memory. The rate limiter in the web layer counts by
/// the address a request came from, which is the right thing to count for a flood and the
/// wrong thing for this: somebody who wants to fill one person's inbox with password letters
/// from this firm's domain moves between addresses, and a counter that resets when the
/// container restarts is not a limit at all. This counts by the address the mail would go
/// to, and survives a restart.
///
/// Modelled on <see cref="SignInRecord"/> statement for statement, and for the same reasons:
/// no foreign key, because a request about an address that matches no account has no account
/// to point at, and not <c>IAuditable</c>, because the trail is for acts on business records
/// and this would bury them.
/// </remarks>
public sealed class RecoveryAsk
{
    private RecoveryAsk()
    {
        Email = string.Empty;
    }

    private RecoveryAsk(
        Guid? userId,
        string email,
        RecoveryOutcome outcome,
        DateTimeOffset at,
        string? ipAddress,
        string? userAgent)
    {
        Id = Guid.CreateVersion7();
        UserId = userId;
        Email = email;
        Outcome = outcome;
        At = at;
        IpAddress = ipAddress;
        UserAgent = userAgent;
    }

    /// <summary>
    /// Record an ask.
    /// </summary>
    /// <remarks>
    /// Everything is truncated to the column it lands in rather than refused. A request that
    /// failed to save because somebody sent a four-kilobyte user agent is a request that
    /// went unthrottled and unrecorded, which is precisely the one worth keeping.
    /// </remarks>
    public static RecoveryAsk For(
        Guid? userId,
        string email,
        RecoveryOutcome outcome,
        DateTimeOffset at,
        string? ipAddress = null,
        string? userAgent = null) =>
        new(userId,
            Normalised(email),
            outcome,
            at,
            Trim(ipAddress, 45),
            Trim(userAgent, 400));

    /// <summary>
    /// The address as a key, upper-cased the way Identity normalises it.
    /// </summary>
    /// <remarks>
    /// Not a tidiness. ASP.NET Identity resolves an account through its normalised address, so
    /// mesh@x, Mesh@x and MESH@x are one account — and a throttle that stored what somebody
    /// typed would give them three separate buckets. Anybody who noticed could then fill one
    /// inbox with password letters from this firm's own domain by varying the case, walking
    /// straight past the limit this table exists to impose.
    ///
    /// Upper-cased rather than lower, to match what Identity puts in NormalizedEmail, so the
    /// two agree about what one address is.
    /// </remarks>
    public static string Normalised(string? email) =>
        (Trim(email, 255) ?? string.Empty).ToUpperInvariant();

    public Guid Id { get; private init; }

    /// <summary>Null when the address matched no account.</summary>
    public Guid? UserId { get; private init; }

    /// <summary>
    /// The address asked about, normalised — a key, not a transcript of what was typed.
    /// </summary>
    /// <remarks>
    /// Kept even when it matched no account, because those rows are the ones that show
    /// somebody working through a list. Normalised because it is what the throttle counts on;
    /// see <see cref="Normalised"/>.
    /// </remarks>
    public string Email { get; private init; }

    public RecoveryOutcome Outcome { get; private init; }

    public DateTimeOffset At { get; private init; }

    public string? IpAddress { get; private init; }

    public string? UserAgent { get; private init; }

    private static string? Trim(string? value, int length)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        return trimmed.Length <= length ? trimmed : trimmed[..length];
    }
}
