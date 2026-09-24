using JiranisokoTech.Application.Abstractions;
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
         *
         * Only runs the scheduler itself started count. A person pressing "run it now" on
         * the machinery screen must not move the timetable: somebody checking a suspect
         * job at 14:00 would otherwise silently push its real daily run from nine the next
         * morning to two in the afternoon, and would have no way of knowing they had. The
         * cost of ignoring their run is that the job may run twice, which every job here
         * is required to survive anyway — a process dying between the work and the record
         * produces exactly the same thing.
         */
        var names = jobs.Select(job => job.Name).ToList();

        var lastRuns = await database.Set<JobRun>()
            .AsNoTracking()
            .Where(run => names.Contains(run.Job) && run.AskedBy == null)
            .GroupBy(run => run.Job)
            .Select(group => new { Job = group.Key, At = group.Max(run => run.At) })
            .ToDictionaryAsync(entry => entry.Job, entry => entry.At, stoppingToken);

        foreach (var job in jobs)
        {
            if (lastRuns.TryGetValue(job.Name, out var last) && clock.Now - last < job.Every)
            {
                continue;
            }

            await RunAsync(job.Name, scope.ServiceProvider, stoppingToken);
        }
    }

    /// <summary>
    /// Runs one job in a scope of its own.
    /// </summary>
    /// <remarks>
    /// Its own scope per job. They share a sweep but not a DbContext: one job whose change
    /// tracker is full of a thousand certifications must not make the next one slow, and a
    /// job that leaves the context in a bad state must not take the others with it. That
    /// is also why the job is resolved by name from inside the new scope rather than
    /// handed across — the instance the sweep listed holds the sweep's context.
    ///
    /// What actually happens to it, and what gets written down, is
    /// <see cref="JobRunner"/>'s. It was lifted out of here so that the "run it now"
    /// button on the machinery screen cannot record a run differently from this loop.
    /// </remarks>
    private static async Task RunAsync(
        string name, IServiceProvider services, CancellationToken stoppingToken)
    {
        using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateScope();

        var runner = scope.ServiceProvider.GetRequiredService<JobRunner>();

        await runner.RunAsync(name, askedBy: null, stoppingToken);
    }
}
