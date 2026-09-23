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
        string job, JobOutcome outcome, string detail, DateTimeOffset at, TimeSpan took)
    {
        Id = Guid.CreateVersion7();
        Job = job;
        Outcome = outcome;
        Detail = detail;
        At = at;
        Milliseconds = (long)took.TotalMilliseconds;
    }

    public static JobRun Ran(string job, string detail, DateTimeOffset at, TimeSpan took) =>
        new(job, JobOutcome.Ran, detail, at, took);

    public static JobRun Failed(string job, string why, DateTimeOffset at, TimeSpan took) =>
        new(job, JobOutcome.Failed, why, at, took);

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
}
