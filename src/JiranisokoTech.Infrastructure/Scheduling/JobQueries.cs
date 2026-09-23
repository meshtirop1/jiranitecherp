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
            })
            .ToDictionaryAsync(entry => entry.Job, entry => entry.At, cancellationToken);

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

                return new JobState(
                    job.Name,
                    job.Description,
                    job.Every,
                    runs.TryGetValue(job.Name, out var at) ? at : null,
                    last?.Outcome,
                    last?.Detail,
                    last?.Milliseconds,
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
    JobOutcome? LastOutcome,
    string? LastDetail,
    long? LastMilliseconds,
    DateTimeOffset Now)
{
    /// <summary>
    /// Has it run recently enough?
    /// </summary>
    /// <remarks>
    /// Twice its interval, not once. A daily job that runs at 09:01 one morning and 09:02
    /// the next has been late by a minute and is not a problem; one that has not run in
    /// two days has stopped. The slack is what stops the screen crying wolf.
    /// </remarks>
    public bool IsOverdue =>
        LastAt is not { } last || Now - last > Every + Every;

    /// <summary>It has never run at all, which is different from being late.</summary>
    public bool HasNeverRun => LastAt is null;

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
