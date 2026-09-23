using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Observability;
using JiranisokoTech.Infrastructure.Observability;
using JiranisokoTech.Infrastructure.Scheduling;

namespace JiranisokoTech.Tests.Infrastructure;

/// <summary>
/// The scheduler, the run history and the counters.
/// </summary>
/// <remarks>
/// The scheduler's loop is not tested by starting it and waiting — a background service
/// that contains its own timer can only be tested that way, and the result is a suite
/// that is slow when it passes and mystifying when it fails. What is tested is everything
/// the loop decides with: when a job is due, when it is overdue, and what the history
/// says afterwards.
/// </remarks>
public class SchedulerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A job is overdue after twice its interval, not once.
    /// </summary>
    /// <remarks>
    /// The slack is what stops the monitoring screen crying wolf. A daily job that ran at
    /// 09:01 one morning and 09:02 the next is a minute late and not a problem; one that
    /// has not run in two days has stopped, and those want different reactions.
    /// </remarks>
    [Fact]
    public void A_job_is_overdue_only_after_twice_its_interval()
    {
        var daily = TimeSpan.FromDays(1);

        Assert.False(State(daily, Now.AddDays(-1).AddMinutes(-1)).IsOverdue);
        Assert.False(State(daily, Now.AddDays(-2).AddMinutes(1)).IsOverdue);
        Assert.True(State(daily, Now.AddDays(-2).AddMinutes(-1)).IsOverdue);
    }

    /// <summary>
    /// Never having run is distinguishable from being late.
    /// </summary>
    /// <remarks>
    /// A job that has never run is usually one just deployed; one that is late has stopped.
    /// Showing both as "overdue" would mean every deploy lights the screen red for an hour.
    /// </remarks>
    [Fact]
    public void Never_having_run_is_not_the_same_as_being_late()
    {
        var fresh = State(TimeSpan.FromDays(1), null);

        Assert.True(fresh.HasNeverRun);
        Assert.True(fresh.IsOverdue);

        Assert.False(State(TimeSpan.FromDays(1), Now.AddHours(-1)).HasNeverRun);
    }

    /// <summary>
    /// A run records what it found, not only that it ran.
    /// </summary>
    /// <remarks>
    /// "Nothing lapses in the next two months" and "three lapsing; told two" are both
    /// useful and only one is a number. A history of zeroes tells nobody whether a job is
    /// working or merely running.
    /// </remarks>
    [Fact]
    public void A_run_records_what_it_found()
    {
        var ran = JobRun.Ran(
            "qualifications.lapsing", "Nothing lapses in the next two months.",
            Now, TimeSpan.FromMilliseconds(120));

        Assert.Equal(JobOutcome.Ran, ran.Outcome);
        Assert.Equal(120, ran.Milliseconds);
        Assert.Contains("Nothing lapses", ran.Detail);

        var failed = JobRun.Failed(
            "contracts.expiring", "The database was unreachable.", Now, TimeSpan.Zero);

        Assert.Equal(JobOutcome.Failed, failed.Outcome);
    }

    /// <summary>
    /// Waiting and given-up are counted apart.
    /// </summary>
    /// <remarks>
    /// They want opposite responses. Something waiting will sort itself out; something
    /// given up on never will until a person does something, and a screen that added them
    /// together would hide the second inside the first.
    /// </remarks>
    [Fact]
    public void Waiting_and_given_up_are_counted_apart()
    {
        var quiet = new QueueDepths(12, 0, 3, 0, 1, 0);

        Assert.False(quiet.AnythingNeedsSomebody);
        Assert.Equal(0, quiet.TotalAbandoned);

        var stuck = quiet with { DeliveriesDeadLettered = 2 };

        Assert.True(stuck.AnythingNeedsSomebody);
        Assert.Equal(2, stuck.TotalAbandoned);
    }

    /// <summary>
    /// Every job's interval is at least an hour.
    /// </summary>
    /// <remarks>
    /// The scheduler wakes once a minute, so a job asking for less would run late every
    /// time and its history would be a list of near misses. Asserted rather than
    /// documented, because the next job somebody adds is where it would be got wrong.
    /// </remarks>
    [Fact]
    public void No_job_asks_to_run_more_often_than_the_scheduler_wakes()
    {
        IRecurringJob[] jobs =
        [
            new WarnAboutLapsingQualifications(null!, null!, null!),
            new WarnAboutExpiringContracts(null!, null!, null!),
            new PruneJobHistory(null!, null!),
        ];

        Assert.All(jobs, job => Assert.True(job.Every >= TimeSpan.FromHours(1)));
    }

    /// <summary>
    /// Every job has a name and says what it is for.
    /// </summary>
    /// <remarks>
    /// The name keys the run history, so it has to be stable and unique — two jobs sharing
    /// one would make each look as though it ran twice as often as it did. The description
    /// is what the monitoring screen shows, and a job nobody can identify is one nobody
    /// notices has stopped.
    /// </remarks>
    [Fact]
    public void Every_job_is_named_and_described()
    {
        IRecurringJob[] jobs =
        [
            new WarnAboutLapsingQualifications(null!, null!, null!),
            new WarnAboutExpiringContracts(null!, null!, null!),
            new PruneJobHistory(null!, null!),
        ];

        Assert.All(jobs, job =>
        {
            Assert.False(string.IsNullOrWhiteSpace(job.Name));
            Assert.False(string.IsNullOrWhiteSpace(job.Description));
        });

        Assert.Equal(
            jobs.Length, jobs.Select(job => job.Name).Distinct(StringComparer.Ordinal).Count());
    }

    // --- the counters -------------------------------------------------------

    /// <summary>
    /// A measurement is counted, and its tags become a separate series.
    /// </summary>
    /// <remarks>
    /// The whole of what the reader does. Counting two differently-tagged measurements
    /// into one total would mean a dashboard that could not tell a GitHub delivery from a
    /// GitLab one.
    /// </remarks>
    [Fact]
    public async Task Measurements_are_totalled_and_tags_kept_apart()
    {
        using var reader = new MetricsReader();
        await reader.StartAsync(CancellationToken.None);

        Telemetry.DeliveriesReceived.Add(
            2, new KeyValuePair<string, object?>("provider", "GitHub"));
        Telemetry.DeliveriesReceived.Add(
            3, new KeyValuePair<string, object?>("provider", "GitHub"));
        Telemetry.DeliveriesReceived.Add(
            1, new KeyValuePair<string, object?>("provider", "GitLab"));

        var page = reader.Scrape();

        await reader.StopAsync(CancellationToken.None);

        // Dots become underscores, which the scrape format requires.
        Assert.Contains("jiranisoko_webhooks_received", page);

        Assert.Contains("provider=\"GitHub\"", page);
        Assert.Contains("provider=\"GitLab\"", page);

        // And the two GitHub measurements added up rather than replacing each other.
        Assert.Contains("5", page);
    }

    /// <summary>
    /// The scrape carries the type and the description a collector expects.
    /// </summary>
    [Fact]
    public async Task The_scrape_describes_what_it_is_reporting()
    {
        using var reader = new MetricsReader();
        await reader.StartAsync(CancellationToken.None);

        Telemetry.JobsRun.Add(1, new KeyValuePair<string, object?>("job", "jobs.prune"));

        var page = reader.Scrape();

        await reader.StopAsync(CancellationToken.None);

        Assert.Contains("# TYPE jiranisoko_jobs_run counter", page);
        Assert.Contains("# HELP jiranisoko_jobs_run", page);
    }

    /// <summary>
    /// The help and type lines appear once per metric, not once per series.
    /// </summary>
    /// <remarks>
    /// Found by scraping the running container rather than by any test, which is why there
    /// is now a test. The first version emitted them beside every labelled series, so a
    /// metric with three providers carried three identical HELP lines — a duplicate
    /// declaration, which a collector rejects the whole family over. The output looked
    /// plausible and was not ingestible.
    /// </remarks>
    [Fact]
    public async Task A_metric_declares_itself_once_however_many_series_it_has()
    {
        using var reader = new MetricsReader();
        await reader.StartAsync(CancellationToken.None);

        foreach (var provider in new[] { "GitHub", "GitLab", "Bitbucket" })
        {
            Telemetry.DeliveriesRefused.Add(
                1, new KeyValuePair<string, object?>("provider", provider));
        }

        var page = reader.Scrape();

        await reader.StopAsync(CancellationToken.None);

        var lines = page.ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(
            1,
            lines.Count(line => line.StartsWith(
                "# HELP jiranisoko_webhooks_refused", StringComparison.Ordinal)));

        Assert.Equal(
            1,
            lines.Count(line => line.StartsWith(
                "# TYPE jiranisoko_webhooks_refused", StringComparison.Ordinal)));

        // And all three series are still there under that one declaration.
        Assert.Equal(
            3,
            lines.Count(line => line.StartsWith(
                "jiranisoko_webhooks_refused{", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A histogram is reported as a total and named as one.
    /// </summary>
    /// <remarks>
    /// This reader keeps sums rather than buckets, so declaring one a histogram would
    /// promise quantiles that are not in the payload. A _seconds_total counter is what a
    /// sum of durations honestly is.
    /// </remarks>
    [Fact]
    public async Task A_duration_is_reported_as_a_total_and_says_so()
    {
        using var reader = new MetricsReader();
        await reader.StartAsync(CancellationToken.None);

        Telemetry.JobDuration.Record(
            1.5, new KeyValuePair<string, object?>("job", "a.job"));

        var page = reader.Scrape();

        await reader.StopAsync(CancellationToken.None);

        Assert.Contains("jiranisoko_jobs_duration_seconds_total", page);
        Assert.DoesNotContain("# TYPE jiranisoko_jobs_duration histogram", page);
    }

    private static JobState State(TimeSpan every, DateTimeOffset? lastAt) =>
        new("a.job", "Does something.", every, lastAt, null, null, null, Now);
}
