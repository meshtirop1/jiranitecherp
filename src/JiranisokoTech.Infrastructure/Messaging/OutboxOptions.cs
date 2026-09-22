namespace JiranisokoTech.Infrastructure.Messaging;

/// <summary>
/// How hard the dispatcher tries, and how often.
/// </summary>
/// <remarks>
/// Every value here is a trade somebody may need to change on a live system
/// without a deployment, which is why they are configuration rather than
/// constants.
/// </remarks>
public sealed class OutboxOptions
{
    public const string Section = "Outbox";

    /// <summary>
    /// How long to wait between passes when the last one found nothing.
    /// </summary>
    /// <remarks>
    /// Five seconds is the delay somebody waits for an email after being hired.
    /// Shorter costs a query per instance per interval against an index that is
    /// nearly always empty; longer starts to feel like the system is broken.
    /// A pass that finds a full batch does not wait at all — it goes straight
    /// round again, so a backlog drains at the speed of the handlers rather than
    /// the speed of this clock.
    /// </remarks>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How many messages one pass takes.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// How many failures before a message is given up on.
    /// </summary>
    /// <remarks>
    /// With the backoff below, eight attempts span roughly four hours — long
    /// enough to ride out a mail server being down or a deployment, short enough
    /// that a genuinely broken handler is not still writing errors next week.
    /// </remarks>
    public int MaxAttempts { get; set; } = 8;

    /// <summary>
    /// The first wait after a failure. It doubles with each attempt.
    /// </summary>
    public TimeSpan FirstRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The longest a retry ever waits, so doubling does not run away.
    /// </summary>
    public TimeSpan MaximumRetryDelay { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How long a claim is honoured before another dispatcher may take the
    /// message.
    /// </summary>
    /// <remarks>
    /// This is the window in which a process that dies mid-dispatch leaves a
    /// message stranded. It must be comfortably longer than the slowest handler,
    /// because a claim that expires while the original dispatcher is still
    /// working means two of them are running the same handler at once.
    /// </remarks>
    public TimeSpan ClaimTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The wait after a pass that failed outright — the database being
    /// unreachable, rather than a handler throwing.
    /// </summary>
    public TimeSpan ErrorBackoff { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a delivered message is kept before it is deleted.
    /// </summary>
    /// <remarks>
    /// Every change in the system writes rows here, and nothing ever reads a
    /// delivered one again — so without a sweep this becomes the largest table
    /// in the database, made entirely of things that already happened. A month
    /// is long enough to answer "was that email actually sent?" about anything
    /// anybody still remembers.
    ///
    /// Abandoned messages are never swept. They are the ones somebody has to
    /// look at, they are rare, and deleting the evidence of a failure on a timer
    /// is how a system loses the only record that it went wrong.
    /// </remarks>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>How often to sweep. Rarely: it is a delete over a date range.</summary>
    public TimeSpan PruneInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How long to wait after a failure with this many attempts behind it.
    /// </summary>
    public TimeSpan RetryDelayAfter(int attempts)
    {
        if (attempts <= 1)
        {
            return FirstRetryDelay;
        }

        // Doubling, in ticks, with the shift done on a long so that a large
        // attempt count cannot overflow into a negative delay and produce a
        // message that retries instantly forever.
        var shift = Math.Min(attempts - 1, 32);
        var ticks = FirstRetryDelay.Ticks * (1L << shift);

        return ticks <= 0 || ticks > MaximumRetryDelay.Ticks
            ? MaximumRetryDelay
            : TimeSpan.FromTicks(ticks);
    }
}
