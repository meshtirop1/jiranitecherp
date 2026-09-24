using System.Diagnostics;
using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Observability;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace JiranisokoTech.Infrastructure.Scheduling;

/// <summary>
/// Runs one job and writes down what came of it.
/// </summary>
/// <remarks>
/// This used to be a private method on <see cref="Scheduler"/>, and it was lifted out so
/// that a person pressing "run it now" produces a run recorded in exactly the same way as
/// one the scheduler started — same span, same counters, same row, failures included.
/// Two code paths writing job history would have drifted within a release, and the
/// drifted one would be the one nobody tested, because nobody tests a button they added
/// to a monitoring screen.
///
/// <b>Scoped, and meant to be resolved inside a scope of its own per run.</b> The
/// scheduler gives each job a fresh scope, so one job whose change tracker is full of a
/// thousand certifications does not make the next one slow, and a job that leaves its
/// context in a bad state does not take the others with it. A run asked for from a page
/// already has the request's scope, which is that same property for free.
/// </remarks>
public sealed class JobRunner(
    AppDbContext database,
    IEnumerable<IRecurringJob> jobs,
    IClock clock,
    ILogger<JobRunner> logger)
{
    /// <summary>
    /// Run the named job, recording who asked.
    /// </summary>
    /// <param name="askedBy">
    /// The person who pressed the button, or null when the scheduler is running it on its
    /// own. See <see cref="JobRun.AskedBy"/> for why the difference is kept.
    /// </param>
    /// <returns>The run that was recorded, or null when no job goes by that name.</returns>
    public async Task<JobRun?> RunAsync(
        string name, string? askedBy, CancellationToken cancellationToken = default)
    {
        var job = jobs.FirstOrDefault(one => one.Name == name);

        if (job is null)
        {
            /*
             * Not an exception. The only caller that can pass an unknown name is a form
             * post naming a job that was removed from the code between the page rendering
             * and the button being pressed, and a five-hundred page for that is worse than
             * the screen saying it could not find it.
             */
            logger.LogWarning("No job is registered under the name {Job}.", name);
            return null;
        }

        using var span = Telemetry.Source.StartActivity($"job {job.Name}");
        var stopwatch = Stopwatch.StartNew();

        JobRun run;

        try
        {
            var detail = await job.RunAsync(cancellationToken);

            stopwatch.Stop();
            run = JobRun.Ran(job.Name, detail, clock.Now, stopwatch.Elapsed, askedBy);

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

            run = JobRun.Failed(
                job.Name, Short(exception), clock.Now, stopwatch.Elapsed, askedBy);

            Telemetry.JobsFailed.Add(1, new KeyValuePair<string, object?>("job", job.Name));

            logger.LogError(exception, "Job {Job} failed.", job.Name);
        }

        Telemetry.JobDuration.Record(
            stopwatch.Elapsed.TotalSeconds, new KeyValuePair<string, object?>("job", job.Name));

        database.JobRuns.Add(run);

        /*
         * Saved even when the job failed, because a failure nobody recorded is
         * indistinguishable from a job that never ran — and those two want opposite
         * responses from whoever reads the screen.
         *
         * Saved with CancellationToken.None rather than the caller's token, which the
         * original version of this did not do. A shutdown mid-job cancels the token, and
         * passing it here means the one run whose record matters most — the one that was
         * interrupted — is the one not written down.
         */
        await database.SaveChangesAsync(CancellationToken.None);

        return run;
    }

    private static string Short(Exception exception) =>
        exception.Message.Length > 500 ? exception.Message[..500] : exception.Message;
}
