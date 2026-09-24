using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Domain.Integrations;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Scheduling;

/// <summary>
/// What the machinery is doing, for the one screen that watches it.
/// </summary>
/// <remarks>
/// Section 45 asked for job monitoring. The three queues in this system already knew
/// their own state and nothing brought the three together, so noticing that one had
/// stopped meant opening three screens and knowing to look.
///
/// <b>Depths are asked of the database, never counted in memory.</b> A counter tracking
/// queue depth drifts the first time a process restarts mid-batch, and a wrong depth on a
/// monitoring screen is worse than no depth: somebody acts on it.
/// </remarks>
public sealed class JobQueries(
    AppDbContext database,
    IEnumerable<IRecurringJob> jobs,
    IClock clock)
{
    public async Task<List<JobState>> JobsAsync(CancellationToken cancellationToken = default)
    {
        var registered = jobs.ToList();
        var names = registered.Select(job => job.Name).ToList();

        var runs = await database.JobRuns
            .AsNoTracking()
            .Where(run => names.Contains(run.Job))
            .GroupBy(run => run.Job)
            .Select(group => new
            {
                Job = group.Key,
                At = group.Max(run => run.At),

                /*
                 * The last SCHEDULED run, kept apart from the last run of any kind, and
                 * this is the whole reason JobRun records who asked.
                 *
                 * Lateness is measured against this one. Somebody who suspects a job has
                 * stopped presses "run it now" to find out — and if that run counted, the
                 * overdue badge would clear, so the act of checking would erase the
                 * evidence. A dead scheduler would look healthy for exactly as long as
                 * somebody kept pressing the button.
                 */
                Scheduled = group
                    .Where(run => run.AskedBy == null)
                    .Max(run => (DateTimeOffset?)run.At),
            })
            .ToDictionaryAsync(entry => entry.Job, entry => entry, cancellationToken);

        var latest = await database.JobRuns
            .AsNoTracking()
            .Where(run => names.Contains(run.Job))
            .OrderByDescending(run => run.At)
            .Take(registered.Count * 4)
            .ToListAsync(cancellationToken);

        return
        [
            .. registered.Select(job =>
            {
                var last = latest
                    .Where(run => run.Job == job.Name)
                    .OrderByDescending(run => run.At)
                    .FirstOrDefault();

                var seen = runs.GetValueOrDefault(job.Name);

                return new JobState(
                    job.Name,
                    job.Description,
                    job.Every,
                    seen is null ? null : seen.At,
                    seen?.Scheduled,
                    last?.Outcome,
                    last?.Detail,
                    last?.Milliseconds,
                    last?.AskedBy,
                    clock.Now);
            }),
        ];
    }

    /// <summary>The recent run history, newest first.</summary>
    public Task<List<JobRun>> HistoryAsync(
        int take = 50, CancellationToken cancellationToken = default) =>
        database.JobRuns
            .AsNoTracking()
            .OrderByDescending(run => run.At)
            .Take(take)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// How deep each queue is, right now.
    /// </summary>
    /// <remarks>
    /// Three queues that exist for three different reasons, brought together because the
    /// question somebody asks is "is anything stuck" and not "is the outbox stuck".
    /// </remarks>
    public async Task<QueueDepths> DepthsAsync(CancellationToken cancellationToken = default)
    {
        var outboxWaiting = await database.Outbox
            .CountAsync(
                message => message.DispatchedAt == null && message.AbandonedAt == null,
                cancellationToken);

        var outboxAbandoned = await database.Outbox
            .CountAsync(message => message.AbandonedAt != null, cancellationToken);

        var deliveriesWaiting = await database.Deliveries
            .CountAsync(
                delivery => delivery.Status == DeliveryStatus.Received
                    || delivery.Status == DeliveryStatus.Failed,
                cancellationToken);

        var deliveriesDead = await database.Deliveries
            .CountAsync(delivery => delivery.Status == DeliveryStatus.DeadLettered,
                cancellationToken);

        var outWaiting = await database.OutboundDeliveries
            .CountAsync(
                one => one.Status == OutboundStatus.Waiting || one.Status == OutboundStatus.Failed,
                cancellationToken);

        var outDead = await database.OutboundDeliveries
            .CountAsync(one => one.Status == OutboundStatus.DeadLettered, cancellationToken);

        return new QueueDepths(
            outboxWaiting, outboxAbandoned,
            deliveriesWaiting, deliveriesDead,
            outWaiting, outDead);
    }
}

/// <summary>One job, and whether it is keeping up.</summary>
public sealed record JobState(
    string Name,
    string Description,
    TimeSpan Every,
    DateTimeOffset? LastAt,
    DateTimeOffset? LastScheduledAt,
    JobOutcome? LastOutcome,
    string? LastDetail,
    long? LastMilliseconds,
    string? LastAskedBy,
    DateTimeOffset Now)
{
    /// <summary>
    /// Has the scheduler run it recently enough?
    /// </summary>
    /// <remarks>
    /// Twice its interval, not once. A daily job that runs at 09:01 one morning and 09:02
    /// the next has been late by a minute and is not a problem; one that has not run in
    /// two days has stopped. The slack is what stops the screen crying wolf.
    ///
    /// Measured against <see cref="LastScheduledAt"/> rather than <see cref="LastAt"/>, so
    /// that running a job by hand cannot make a stopped scheduler look alive. See the
    /// remark on the query that fills it in.
    /// </remarks>
    public bool IsOverdue =>
        LastScheduledAt is not { } last || Now - last > Every + Every;

    /// <summary>It has never run at all, which is different from being late.</summary>
    public bool HasNeverRun => LastAt is null;

    /// <summary>
    /// The scheduler has never run it, although somebody has.
    /// </summary>
    /// <remarks>
    /// Worth saying in its own words on the screen. "Overdue" next to a run that finished
    /// two minutes ago reads as a bug in the screen, and somebody dismisses it — when in
    /// fact it is the screen correctly reporting that the only thing running this job is a
    /// person.
    /// </remarks>
    public bool OnlyEverByHand => LastAt is not null && LastScheduledAt is null;

    /// <summary>The most recent run was one somebody asked for.</summary>
    public bool LastWasByHand => LastAskedBy is not null;

    public bool LastFailed => LastOutcome == JobOutcome.Failed;
}

/// <summary>
/// How much is waiting, and how much has given up.
/// </summary>
/// <remarks>
/// Waiting and abandoned are kept apart everywhere they appear, because they want
/// opposite responses: something waiting will sort itself out, and something abandoned
/// never will until a person does something.
/// </remarks>
public sealed record QueueDepths(
    int OutboxWaiting,
    int OutboxAbandoned,
    int DeliveriesWaiting,
    int DeliveriesDeadLettered,
    int NotificationsWaiting,
    int NotificationsDeadLettered)
{
    public int TotalAbandoned =>
        OutboxAbandoned + DeliveriesDeadLettered + NotificationsDeadLettered;

    public bool AnythingNeedsSomebody => TotalAbandoned > 0;
}
