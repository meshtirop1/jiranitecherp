using System.Diagnostics;
using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Observability;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JiranisokoTech.Infrastructure.Scheduling;

/// <summary>
/// Runs the recurring jobs, and writes down what came of each.
/// </summary>
/// <remarks>
/// Deliberately the simplest thing that is honest.
///
/// <b>It schedules from the last run recorded in the database, not from process start.</b>
/// A scheduler that counted from startup would run everything again on every deploy, and
/// on a busy week that means a daily sweep running six times — each one sending the same
/// reminders.
///
/// <b>It does not attempt to be the only one running.</b> This firm runs one container.
/// If a second is ever started, two schedulers would both run the daily sweep, and that
/// is survivable because every job is written to be safe run twice — which is required of
/// them anyway, since a process dying between the work and the record produces the same
/// thing. What is not attempted is a lock: a distributed lease is a real mechanism with
/// real failure modes, and adding one for a problem this deployment does not have would
/// be inventing an outage.
///
/// <b>A job that throws does not stop the others</b>, and does not stop the loop. The
/// failure is recorded against that job so it shows up on the monitoring screen, and the
/// next interval tries again.
/// </remarks>
public sealed class Scheduler(
    IServiceScopeFactory scopes,
    ILogger<Scheduler> logger)
    : BackgroundService
{
    /// <summary>
    /// How often the loop wakes to see whether anything is due.
    /// </summary>
    /// <remarks>
    /// A minute. Nothing here is due to the second — the shortest interval any job asks
    /// for is an hour — and waking more often would spend a query on finding nothing.
    /// </remarks>
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long to wait before the first sweep.
    /// </summary>
    /// <remarks>
    /// Half a minute, so that starting up does not compete with the migrations, the
    /// seeding and the first requests. Nothing is so urgent that it cannot wait for the
    /// process to finish becoming ready.
    /// </remarks>
    private static readonly TimeSpan SettleIn = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(SettleIn, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using (var scope = scopes.CreateScope())
        {
            var jobs = scope.ServiceProvider.GetServices<IRecurringJob>().ToList();

            logger.LogInformation(
                "Scheduler started with {Count} job(s): {Names}.",
                jobs.Count,
                string.Join(", ", jobs.Select(job => job.Name)));
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // The sweep itself failed — the database unreachable, most likely. A
                // scheduler that exited here would leave a container that is up, healthy
                // and quietly doing none of its recurring work.
                logger.LogError(exception, "A scheduler sweep failed. It will be tried again.");
            }

            try
            {
                await Task.Delay(Tick, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Scheduler stopped.");
    }

    private async Task SweepAsync(CancellationToken stoppingToken)
    {
        using var scope = scopes.CreateScope();

        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var jobs = scope.ServiceProvider.GetServices<IRecurringJob>().ToList();

        if (jobs.Count == 0)
        {
            return;
        }

        /*
         * The last run of each job, read from the database rather than remembered. A
         * scheduler that counted from process start would run every daily sweep again on
         * every deploy — six times in a busy week, each one sending the same reminders.
         */
        var names = jobs.Select(job => job.Name).ToList();

        var lastRuns = await database.Set<JobRun>()
            .AsNoTracking()
            .Where(run => names.Contains(run.Job))
            .GroupBy(run => run.Job)
            .Select(group => new { Job = group.Key, At = group.Max(run => run.At) })
            .ToDictionaryAsync(entry => entry.Job, entry => entry.At, stoppingToken);

        foreach (var job in jobs)
        {
            if (lastRuns.TryGetValue(job.Name, out var last) && clock.Now - last < job.Every)
            {
                continue;
            }

            await RunAsync(job, scope.ServiceProvider, clock, stoppingToken);
        }
    }

    private async Task RunAsync(
        IRecurringJob job,
        IServiceProvider services,
        IClock clock,
        CancellationToken stoppingToken)
    {
        /*
         * Its own scope per job. They share a sweep but not a DbContext: one job whose
         * change tracker is full of a thousand certifications must not make the next one
         * slow, and a job that leaves the context in a bad state must not take the others
         * with it.
         */
        using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateScope();

        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        using var span = Telemetry.Source.StartActivity($"job {job.Name}");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var detail = await ScopedAsync(job, scope.ServiceProvider, stoppingToken);

            stopwatch.Stop();

            database.Set<JobRun>().Add(
                JobRun.Ran(job.Name, detail, clock.Now, stopwatch.Elapsed));

            Telemetry.JobsRun.Add(1, new KeyValuePair<string, object?>("job", job.Name));

            logger.LogInformation(
                "Job {Job} ran in {Milliseconds}ms: {Detail}",
                job.Name,
                stopwatch.ElapsedMilliseconds,
                detail);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            stopwatch.Stop();
            span?.SetStatus(ActivityStatusCode.Error, exception.Message);

            database.Set<JobRun>().Add(JobRun.Failed(
                job.Name, Short(exception), clock.Now, stopwatch.Elapsed));

            Telemetry.JobsFailed.Add(1, new KeyValuePair<string, object?>("job", job.Name));

            logger.LogError(exception, "Job {Job} failed.", job.Name);
        }

        Telemetry.JobDuration.Record(
            stopwatch.Elapsed.TotalSeconds, new KeyValuePair<string, object?>("job", job.Name));

        /*
         * Saved even when the job failed, because a failure nobody recorded is
         * indistinguishable from a job that never ran — and those want opposite responses.
         */
        await database.SaveChangesAsync(stoppingToken);
    }

    /// <summary>
    /// Resolves the job again inside its own scope before running it.
    /// </summary>
    /// <remarks>
    /// The instance handed to the sweep came from the sweep's scope and holds that scope's
    /// DbContext. Running it there would share a change tracker across every job in the
    /// sweep, which is the thing the separate scopes exist to prevent.
    /// </remarks>
    private static async Task<string> ScopedAsync(
        IRecurringJob job, IServiceProvider services, CancellationToken stoppingToken)
    {
        var fresh = services.GetServices<IRecurringJob>()
            .FirstOrDefault(one => one.Name == job.Name) ?? job;

        return await fresh.RunAsync(stoppingToken);
    }

    private static string Short(Exception exception) =>
        exception.Message.Length > 500 ? exception.Message[..500] : exception.Message;
}
