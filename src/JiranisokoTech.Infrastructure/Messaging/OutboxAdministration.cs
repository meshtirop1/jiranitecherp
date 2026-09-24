using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JiranisokoTech.Infrastructure.Messaging;

/// <summary>
/// Looking at the events queue, and putting back the ones that gave up.
/// </summary>
/// <remarks>
/// The machinery screen has counted abandoned outbox rows since it was built, and its
/// own alert says they "will not be tried again without somebody" — while offering
/// nobody anything to do. Of the three queues on that screen, the other two have had a
/// replay button from the day they were written. This one, the most consequential of the
/// three, had a number.
///
/// It is the most consequential because of what is in it. A dead-lettered webhook
/// delivery means this system does not know something a code host tried to tell it,
/// which is recoverable by asking the host again. An abandoned outbox row means a change
/// that <i>did</i> happen here had a consequence that did not: somebody left and their
/// work was never released, an invoice was paid and no receipt went out. There is
/// nowhere else to ask.
/// </remarks>
public sealed class OutboxAdministration(
    AppDbContext database,
    DomainEventRegistry registry,
    IOptions<OutboxOptions> options,
    IClock clock,
    ILogger<OutboxAdministration> logger)
{
    /// <summary>
    /// How many rows either list shows.
    /// </summary>
    /// <remarks>
    /// Both lists are normally empty, so a cap looks like caution about nothing. It is
    /// not: the case this screen is opened in is the one where a deployment has just
    /// broken a handler, and then every change the firm made that day is in one of these
    /// two lists. Reading all of them to show a page of them is the fault section 77
    /// found five times over.
    /// </remarks>
    public const int MostShown = 100;

    /// <summary>
    /// How many failures the dispatcher allows before it gives up.
    /// </summary>
    /// <remarks>
    /// Read from the configured options rather than written on the screen as a number.
    /// Every value in OutboxOptions is a trade somebody may need to change on a live system
    /// without a deployment, and a monitoring screen stating a stale one is worse than a
    /// screen stating none: it is read by the person deciding whether to wait.
    /// </remarks>
    public int MostAttempts => options.Value.MaxAttempts;

    /// <summary>
    /// The rows that gave up, oldest first.
    /// </summary>
    /// <remarks>
    /// Oldest first rather than newest, which is the opposite of every other list in this
    /// application, and the reason is that these are not records to read — they are work
    /// to redo. Two events from the same aggregate have handlers that may undo each
    /// other, so replaying them in the order they occurred is the only ordering that ends
    /// where the firm actually is.
    /// </remarks>
    public async Task<List<OutboxRow>> AbandonedAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await database.Outbox
            .AsNoTracking()
            .Where(message => message.AbandonedAt != null)
            .OrderBy(message => message.OccurredAt)
            .Take(MostShown)
            .Select(message => new
            {
                message.Id,
                message.Type,
                message.OccurredAt,
                message.AbandonedAt,
                message.Attempts,
                message.Error,
            })
            .ToListAsync(cancellationToken);

        return
        [
            .. rows.Select(row => new OutboxRow(
                row.Id,
                row.Type,
                row.OccurredAt,
                row.AbandonedAt,
                null,
                row.Attempts,
                row.Error,
                registry.Find(row.Type) is not null)),
        ];
    }

    /// <summary>What is still queued, oldest first.</summary>
    public async Task<List<OutboxRow>> WaitingAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await database.Outbox
            .AsNoTracking()
            .Where(message => message.DispatchedAt == null && message.AbandonedAt == null)
            .OrderBy(message => message.OccurredAt)
            .Take(MostShown)
            .Select(message => new
            {
                message.Id,
                message.Type,
                message.OccurredAt,
                message.NextAttemptAt,
                message.Attempts,
                message.Error,
            })
            .ToListAsync(cancellationToken);

        return
        [
            .. rows.Select(row => new OutboxRow(
                row.Id,
                row.Type,
                row.OccurredAt,
                null,
                row.NextAttemptAt,
                row.Attempts,
                row.Error,
                registry.Find(row.Type) is not null)),
        ];
    }

    /// <summary>Put one abandoned message back in the queue.</summary>
    /// <returns>Whether there was an abandoned message with that id to put back.</returns>
    public async Task<bool> ReviveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var message = await database.Outbox
            .FirstOrDefaultAsync(one => one.Id == id, cancellationToken);

        if (message is null || message.AbandonedAt is null)
        {
            return false;
        }

        message.Revive(clock.Now);
        await database.SaveChangesAsync(cancellationToken);

        /*
         * Logged at warning rather than information, and the wording says "by hand" on
         * purpose. Somebody reading the log a week later is trying to work out why a
         * handler ran twice for one change, and a person pressing this button is the
         * answer they will never guess.
         */
        logger.LogWarning(
            "Outbox message {MessageId} of type {Type} was revived by hand.",
            message.Id,
            message.Type);

        return true;
    }

    /// <summary>
    /// Put every abandoned message back, oldest first.
    /// </summary>
    /// <remarks>
    /// The button somebody actually wants, because the shape of this failure is not one
    /// row. A handler broken by a deployment abandons everything that happened while that
    /// deployment was live, and asking somebody to click fifty times is asking them to
    /// miss one.
    ///
    /// Bounded by the same cap as the list, because a person can only mean to revive what
    /// they were shown. A button that quietly took a thousand rows when the page said a
    /// hundred would be doing more than it said.
    /// </remarks>
    public async Task<int> ReviveEverythingAbandonedAsync(
        CancellationToken cancellationToken = default)
    {
        var messages = await database.Outbox
            .Where(message => message.AbandonedAt != null)
            .OrderBy(message => message.OccurredAt)
            .Take(MostShown)
            .ToListAsync(cancellationToken);

        if (messages.Count == 0)
        {
            return 0;
        }

        var now = clock.Now;

        foreach (var message in messages)
        {
            message.Revive(now);
        }

        await database.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            "{Count} abandoned outbox message(s) were revived by hand.", messages.Count);

        return messages.Count;
    }

    /// <summary>How many there are altogether, which is not how many are shown.</summary>
    public async Task<OutboxTotals> CountsAsync(CancellationToken cancellationToken = default)
    {
        var abandoned = await database.Outbox
            .CountAsync(message => message.AbandonedAt != null, cancellationToken);

        var waiting = await database.Outbox
            .CountAsync(
                message => message.DispatchedAt == null && message.AbandonedAt == null,
                cancellationToken);

        return new OutboxTotals(abandoned, waiting);
    }
}

/// <summary>
/// How many rows are in each state, so the screen can say when it is showing a subset.
/// </summary>
/// <remarks>
/// Separate from the lists rather than inferred from their length, because a list capped
/// at a hundred is indistinguishable from a queue of exactly a hundred — and the two want
/// different responses from whoever is reading.
/// </remarks>
public sealed record OutboxTotals(int Abandoned, int Waiting);

/// <summary>One outbox row, as the screen needs it.</summary>
/// <param name="StillInTheCode">
/// Whether the event type stored on this row is still one the registry can rebuild. False
/// means reviving it will abandon it again within seconds, which is worth saying on the
/// screen rather than letting somebody press a button and watch nothing change.
/// </param>
public sealed record OutboxRow(
    Guid Id,
    string Type,
    DateTimeOffset OccurredAt,
    DateTimeOffset? AbandonedAt,
    DateTimeOffset? NextAttemptAt,
    int Attempts,
    string? Error,
    bool StillInTheCode)
{
    /// <summary>
    /// The event's name with its words separated, for a reader rather than a compiler.
    /// </summary>
    /// <remarks>
    /// The stored value is a class name, and the screen shows that too, because somebody
    /// grepping the log for the fault needs the exact string. This is the other half: a
    /// person scanning fifty rows for the one that matters reads "Employee left the firm"
    /// far faster than EmployeeLeftTheFirm.
    /// </remarks>
    public string Said => string.Concat(
        Type.Select((letter, index) => index > 0 && char.IsUpper(letter)
            ? " " + char.ToLowerInvariant(letter)
            : letter.ToString()));
}
