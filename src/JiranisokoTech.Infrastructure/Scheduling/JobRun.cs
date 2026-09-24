namespace JiranisokoTech.Infrastructure.Scheduling;

/// <summary>What became of one run.</summary>
public enum JobOutcome
{
    Ran = 1,
    Failed = 2,
}

/// <summary>
/// One run of a scheduled job.
/// </summary>
/// <remarks>
/// Not a domain entity, for the same reason a sign-in record is not: this is a fact about
/// the machinery rather than about the business, and putting it through the audit trail
/// would bury every act somebody actually performed under a hundred sweeps a day.
///
/// The history is the whole feature. A scheduler with no record of its runs is a
/// scheduler nobody can tell has stopped — and a job that silently stopped three weeks
/// ago is worse than one that never existed, because the firm has been relying on it.
/// </remarks>
public sealed class JobRun
{
    private JobRun()
    {
        Job = string.Empty;
        Detail = string.Empty;
    }

    private JobRun(
        string job,
        JobOutcome outcome,
        string detail,
        DateTimeOffset at,
        TimeSpan took,
        string? askedBy)
    {
        Id = Guid.CreateVersion7();
        Job = job;
        Outcome = outcome;
        Detail = detail;
        At = at;
        Milliseconds = (long)took.TotalMilliseconds;
        AskedBy = askedBy;
    }

    public static JobRun Ran(
        string job,
        string detail,
        DateTimeOffset at,
        TimeSpan took,
        string? askedBy = null) =>
        new(job, JobOutcome.Ran, detail, at, took, askedBy);

    public static JobRun Failed(
        string job,
        string why,
        DateTimeOffset at,
        TimeSpan took,
        string? askedBy = null) =>
        new(job, JobOutcome.Failed, why, at, took, askedBy);

    public Guid Id { get; private init; }

    public string Job { get; private init; }

    public JobOutcome Outcome { get; private init; }

    /// <summary>
    /// What the job found, or why it failed.
    /// </summary>
    /// <remarks>
    /// A sentence rather than a count, because "nothing to do" and "notified three people"
    /// are both useful and only one of them is a number. A history of zeroes tells nobody
    /// whether the job is working or merely running.
    /// </remarks>
    public string Detail { get; private init; }

    public DateTimeOffset At { get; private init; }

    public long Milliseconds { get; private init; }

    /// <summary>
    /// Who pressed the button, or null when the scheduler ran it on its own.
    /// </summary>
    /// <remarks>
    /// The column exists because of what the machinery screen is for. That screen answers
    /// one question — has this job stopped — by comparing the last run against the
    /// interval, and an on-demand run indistinguishable from a scheduled one would answer
    /// it wrongly in the worst direction: somebody presses "run it now" to check a job
    /// they suspect has stopped, the run succeeds, the overdue badge clears, and a dead
    /// scheduler now looks healthy.
    ///
    /// So the two are recorded apart, and <c>JobQueries</c> measures lateness against
    /// scheduled runs only. The history shows both, because "who ran this at 14:07 on a
    /// Tuesday" is exactly the question somebody has when a handler ran twice.
    /// </remarks>
    public string? AskedBy { get; private init; }

    /// <summary>The scheduler ran this one, rather than a person asking for it.</summary>
    public bool WasScheduled => AskedBy is null;
}
