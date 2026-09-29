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
    /// Running a job by hand does not make a stopped scheduler look healthy.
    /// </summary>
    /// <remarks>
    /// The fault this guards against is circular and easy to ship. Somebody sees a job
    /// marked overdue, presses "run it now" to find out whether it still works, the run
    /// succeeds — and if that run counted as the last run, the badge clears. The act of
    /// checking would erase the evidence, and the scheduler could be dead for weeks while
    /// every check came back clean.
    ///
    /// So lateness is measured against the scheduler's own runs, and a job kept alive only
    /// by somebody pressing a button says so in its own words rather than hiding behind
    /// one that reads as a bug in the screen.
    /// </remarks>
    [Fact]
    public void A_run_somebody_asked_for_does_not_clear_the_overdue_badge()
    {
        var daily = TimeSpan.FromDays(1);

        var neglected = new JobState(
            "a.job",
            "Does something.",
            daily,
            LastAt: Now.AddMinutes(-2),
            LastScheduledAt: Now.AddDays(-9),
            LastOutcome: JobOutcome.Ran,
            LastDetail: "Nothing to do.",
            LastMilliseconds: 12,
            LastAskedBy: "Mesh Tirop",
            Now: Now);

        Assert.True(neglected.IsOverdue);
        Assert.False(neglected.HasNeverRun);
        Assert.True(neglected.LastWasByHand);
        Assert.False(neglected.OnlyEverByHand);

        var onlyEverByHand = neglected with { LastScheduledAt = null };

        Assert.True(onlyEverByHand.IsOverdue);
        Assert.True(onlyEverByHand.OnlyEverByHand);

        // And the ordinary case is unaffected: a scheduled run two minutes ago is fine.
        Assert.False((neglected with
        {
            LastScheduledAt = Now.AddMinutes(-2),
            LastAskedBy = null,
        }).IsOverdue);
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
    /// No job asks to run more often than the scheduler wakes.
    /// </summary>
    /// <remarks>
    /// The loop wakes once a minute, so a job asking for less would run late every time and its
    /// history would be a list of near misses.
    ///
    /// <b>This used to assert an hour, over a hand-written list of three jobs.</b> There are
    /// eleven, and two of them ask for less than an hour: the automation release every five
    /// minutes, and section 26's escalation sweep every thirty. Both are deliberate and both are
    /// fine against a one-minute tick — but the test said "every job's interval is at least an
    /// hour" and had been passing for months because neither of them was in its list, and
    /// <c>Scheduler</c>'s own comment repeated the claim. A test that names its subjects tests
    /// whatever was true on the day somebody wrote it down.
    ///
    /// So the set comes from the assembly and the bound is the real one: longer than the tick.
    /// </remarks>
    [Fact]
    public void No_job_asks_to_run_more_often_than_the_scheduler_wakes()
    {
        var tooOften = Jobs()
            .Where(job => job.Every <= TimeSpan.FromMinutes(1))
            .Select(job => $"  {job.GetType().Name} asks for {job.Every}")
            .ToList();

        Assert.True(
            tooOften.Count == 0,
            "The scheduler wakes once a minute, so these jobs would run late every time and "
            + "their history would be a list of near misses:\n" + string.Join('\n', tooOften));
    }

    /// <summary>
    /// Every job has a name and says what it is for.
    /// </summary>
    /// <remarks>
    /// The name keys the run history, so it has to be stable and unique — two jobs sharing one
    /// would make each look as though it ran twice as often as it did. The description is what
    /// the monitoring screen shows, and a job nobody can identify is one nobody notices has
    /// stopped.
    /// </remarks>
    [Fact]
    public void Every_job_is_named_and_described()
    {
        var jobs = Jobs();

        Assert.NotEmpty(jobs);

        Assert.All(jobs, job =>
        {
            Assert.False(string.IsNullOrWhiteSpace(job.Name));
            Assert.False(string.IsNullOrWhiteSpace(job.Description));
        });

        var shared = jobs
            .GroupBy(job => job.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"  {group.Key}: "
                + string.Join(", ", group.Select(job => job.GetType().Name)))
            .ToList();

        Assert.True(
            shared.Count == 0,
            "These jobs share a name, so the run history cannot tell them apart and each looks "
            + "as though it ran twice as often as it did:\n" + string.Join('\n', shared));
    }

    /// <summary>
    /// Every job that exists is one the scheduler has been told about.
    /// </summary>
    /// <remarks>
    /// The fault this catches is the one this codebase keeps producing and already has a test for
    /// at two other levels: a capability built carefully, tested, and never wired to anything. A
    /// job nobody registered runs never, its history stays empty, and the monitoring screen has no
    /// row to be missing — so there is nothing anywhere to look wrong.
    ///
    /// Read out of the source rather than out of a container, because building the real container
    /// needs a database and the question is about a line of registration code.
    /// </remarks>
    [Fact]
    public void Every_job_that_exists_is_registered_with_the_scheduler()
    {
        var wiring = File.ReadAllText(Path.Combine(
            Root(), "src", "JiranisokoTech.Infrastructure", "ServiceCollectionExtensions.cs"));

        var unregistered = Jobs()
            .Select(job => job.GetType().Name)
            .Where(name => !wiring.Contains($"IRecurringJob, {name}>", StringComparison.Ordinal)
                && !wiring.Contains($".{name}>", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            unregistered.Count == 0,
            "These recurring jobs are written and nothing has been told to run them, so they run "
            + "never and the monitoring screen has no row to be missing: "
            + string.Join(", ", unregistered));
    }

    /// <summary>
    /// Every recurring job in the infrastructure assembly, built with nothing in it.
    /// </summary>
    /// <remarks>
    /// Constructed with nulls, which is what the hand-written lists this replaced did too. It is
    /// safe for exactly what is asked of them here: <c>Name</c>, <c>Description</c> and
    /// <c>Every</c> are literals on every one of these, and nothing below calls <c>RunAsync</c>.
    /// </remarks>
    private static List<IRecurringJob> Jobs() =>
        [.. typeof(Scheduler).Assembly
            .GetTypes()
            .Where(type => typeof(IRecurringJob).IsAssignableFrom(type)
                && type is { IsAbstract: false, IsInterface: false })
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .Select(Empty)];

    private static IRecurringJob Empty(Type type)
    {
        var constructor = type.GetConstructors().Single();

        var nothing = constructor.GetParameters()
            .Select(parameter => parameter.ParameterType.IsValueType
                ? Activator.CreateInstance(parameter.ParameterType)
                : null)
            .ToArray();

        return (IRecurringJob)constructor.Invoke(nothing);
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "JiranisokoTech.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
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

    /// <summary>A job the scheduler has been running, and nobody has touched by hand.</summary>
    private static JobState State(TimeSpan every, DateTimeOffset? lastAt) =>
        new("a.job", "Does something.", every, lastAt, lastAt, null, null, null, null, Now);
}
