namespace JiranisokoTech.Infrastructure.Identity;

/// <summary>
/// One attempt to sign in, successful or not.
/// </summary>
/// <remarks>
/// Kept for two different readers. A person checking their own account wants to
/// see where it has been used and spot somewhere they have never been; whoever
/// answers a security question later wants to know when an account was last
/// touched and from where.
///
/// Failures are recorded as well as successes, and that is the more useful half
/// — a burst of failures from one address is the shape of an attack, and an
/// attack nobody recorded is one nobody can describe afterwards.
///
/// Not <c>IAuditable</c>. The audit trail is for acts on business records, and
/// filling it with one entry per sign-in would bury everything else. This is its
/// own table with its own retention.
/// </remarks>
public sealed class SignInRecord
{
    private SignInRecord()
    {
        Email = string.Empty;
    }

    private SignInRecord(
        Guid? userId,
        string email,
        SignInOutcome outcome,
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

    public Guid Id { get; private init; }

    /// <summary>
    /// Null when the address matched no account.
    /// </summary>
    /// <remarks>
    /// Which is worth recording rather than discarding: somebody working
    /// through a list of addresses leaves a trail of these, and it is the only
    /// evidence that the list existed.
    /// </remarks>
    public Guid? UserId { get; private init; }

    /// <summary>
    /// The address that was tried, kept even when it matched nothing.
    /// </summary>
    public string Email { get; private init; }

    public SignInOutcome Outcome { get; private init; }

    public DateTimeOffset At { get; private init; }

    public string? IpAddress { get; private init; }

    /// <summary>
    /// The browser's own description of itself. Truncated, and not to be trusted
    /// for anything but recognition — it is a header a client chooses.
    /// </summary>
    public string? UserAgent { get; private init; }

    public bool Succeeded => Outcome == SignInOutcome.Succeeded;

    public static SignInRecord For(
        Guid? userId,
        string email,
        SignInOutcome outcome,
        DateTimeOffset at,
        string? ipAddress = null,
        string? userAgent = null)
    {
        return new SignInRecord(
            userId,
            Trim(email, 255) ?? string.Empty,
            outcome,
            at,
            Trim(ipAddress, 45),
            Trim(userAgent, 400));
    }

    private static string? Trim(string? value, int length) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Length > length ? value[..length] : value;
}

/// <summary>
/// How an attempt ended.
/// </summary>
/// <remarks>
/// Distinguished here, in the record, but never in what the sign-in page says
/// back. "No such account" and "wrong password" told apart out loud turn the
/// form into a way to discover who has an account.
/// </remarks>
public enum SignInOutcome
{
    Succeeded = 1,

    /// <summary>Wrong password, or an address that matches nothing.</summary>
    Refused = 2,

    /// <summary>Too many failures. Temporary, and its own fact.</summary>
    LockedOut = 3,

    /// <summary>The account exists and has been switched off by somebody.</summary>
    Deactivated = 4,

    /// <summary>Password accepted; a second factor is still owed.</summary>
    SecondFactorRequired = 5,
}
