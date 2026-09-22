using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JiranisokoTech.Infrastructure.Messaging;

/// <summary>
/// Reads the outbox and gives each message to whoever handles it.
/// </summary>
/// <remarks>
/// One pass over a batch, and nothing about a schedule — the loop lives in
/// <see cref="OutboxProcessor"/>. Kept apart so that a test can run exactly one
/// pass and read the result, rather than starting a timer and hoping.
///
/// Delivery is at least once. A dispatcher can publish a message, do the work,
/// and die before recording that it did; the message is then delivered again.
/// Handlers are required to be idempotent, and that requirement is the reason
/// this is safe rather than an apology for it.
/// </remarks>
public sealed class OutboxDispatcher(
    AppDbContext database,
    DomainEventRegistry registry,
    IServiceProvider services,
    IClock clock,
    IOptions<OutboxOptions> options,
    ILogger<OutboxDispatcher> logger)
{
    private static readonly ConcurrentDictionary<Type, MethodInfo> HandleMethods = new();

    /// <summary>
    /// Take one batch and deal with it.
    /// </summary>
    /// <returns>How many messages this pass settled, one way or another.</returns>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var now = clock.Now;

        // One id per pass, so the log can be read as "this run took these
        // messages" and a claim can be recognised as ours.
        var worker = Guid.CreateVersion7();

        var candidates = await CandidatesAsync(now, settings, cancellationToken);
        var settled = 0;

        foreach (var id in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!await ClaimAsync(id, worker, now, settings, cancellationToken))
            {
                // Another dispatcher took it between our read and our claim.
                // Which is the whole point of claiming, so this is not worth a
                // line in the log.
                continue;
            }

            if (await SettleAsync(id, cancellationToken))
            {
                settled++;
            }
        }

        return settled;
    }

    /// <summary>
    /// Delete delivered messages that are old enough to be of no interest.
    /// </summary>
    /// <remarks>
    /// Every change in the system writes a row here and nothing ever reads a
    /// delivered one again, so without this the outbox becomes the largest table
    /// in the database, made entirely of things that already happened.
    ///
    /// Abandoned messages are deliberately left alone. They are the ones
    /// somebody has to look at, and deleting the record of a failure on a timer
    /// is how a system loses the only evidence that it went wrong.
    /// </remarks>
    public async Task<int> PruneAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = clock.Now - options.Value.Retention;

        var swept = await database.Outbox
            .Where(message => message.DispatchedAt != null && message.DispatchedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);

        if (swept > 0)
        {
            logger.LogInformation(
                "Swept {Count} delivered outbox messages older than {Cutoff}.", swept, cutoff);
        }

        return swept;
    }

    private Task<List<Guid>> CandidatesAsync(
        DateTimeOffset now, OutboxOptions settings, CancellationToken cancellationToken)
    {
        var staleBefore = now - settings.ClaimTimeout;

        return database.Outbox
            .Where(message => message.DispatchedAt == null && message.AbandonedAt == null)

            // Either never attempted, or its backoff has run out.
            .Where(message => message.NextAttemptAt == null || message.NextAttemptAt <= now)

            // Unclaimed, or claimed by a process that has not come back. Without
            // the second half, one dispatcher dying mid-batch would strand those
            // messages for good.
            .Where(message => message.ClaimedBy == null || message.ClaimedAt < staleBefore)

            // Oldest first. Handlers are not promised an order, but delivering a
            // backlog newest-first would be a strange thing to explain.
            .OrderBy(message => message.OccurredAt)
            .Take(settings.BatchSize)
            .Select(message => message.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Take a message, if it is still there to be taken.
    /// </summary>
    /// <remarks>
    /// A single conditional UPDATE, and the row count is the answer. Reading the
    /// row and then writing it would let two instances both read it as free
    /// before either wrote — two rejection emails to one candidate, from a
    /// system whose logs both look correct.
    /// </remarks>
    private async Task<bool> ClaimAsync(
        Guid id,
        Guid worker,
        DateTimeOffset now,
        OutboxOptions settings,
        CancellationToken cancellationToken)
    {
        var staleBefore = now - settings.ClaimTimeout;

        var taken = await database.Outbox
            .Where(message => message.Id == id
                && message.DispatchedAt == null
                && message.AbandonedAt == null
                && (message.ClaimedBy == null || message.ClaimedAt < staleBefore))
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(message => message.ClaimedBy, worker)
                    .SetProperty(message => message.ClaimedAt, now),
                cancellationToken);

        return taken == 1;
    }

    private async Task<bool> SettleAsync(Guid id, CancellationToken cancellationToken)
    {
        // Read after claiming, so what is handled is what the claim protects.
        // AsTracking because this row is about to be written back.
        var message = await database.Outbox
            .AsTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (message is null)
        {
            return false;
        }

        var type = registry.Find(message.Type);

        if (type is null)
        {
            /*
             * The code no longer has this event. A release that deletes an event
             * class leaves rows behind that nothing can ever rebuild, and
             * retrying them until the end of time only fills the log. Abandoned,
             * and kept, so somebody can see what was lost.
             */
            return await AbandonAsync(
                message,
                $"No event type named '{message.Type}' exists in this build.",
                cancellationToken);
        }

        object? domainEvent;

        try
        {
            domainEvent = JsonSerializer.Deserialize(
                message.Payload, type, AppDbContext.JsonOptions);
        }
        catch (JsonException exception)
        {
            // A payload that will not parse now will not parse on the tenth
            // attempt either.
            return await AbandonAsync(
                message,
                $"The payload could not be read as {type.Name}: {exception.Message}",
                cancellationToken);
        }

        if (domainEvent is not IDomainEvent rebuilt)
        {
            return await AbandonAsync(
                message,
                $"The payload produced no {type.Name} to hand to a handler.",
                cancellationToken);
        }

        try
        {
            await InvokeHandlersAsync(rebuilt, type, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return await FailAsync(message, exception, cancellationToken);
        }

        message.MarkDispatched(clock.Now);
        await database.SaveChangesAsync(cancellationToken);

        logger.LogDebug("Dispatched {Type} {MessageId}.", message.Type, message.Id);

        return true;
    }

    /// <summary>
    /// Give the event to every handler registered for it.
    /// </summary>
    /// <remarks>
    /// No handler at all is a success, not a failure. An event is a fact, and a
    /// fact nobody has subscribed to yet is still true — marking it dispatched
    /// is what stops the table filling with rows waiting for code that may never
    /// be written.
    ///
    /// If one handler throws, the message is retried and the handlers that
    /// already succeeded run again. That is the reason they are required to be
    /// idempotent, stated where somebody changing this will read it.
    /// </remarks>
    private async Task InvokeHandlersAsync(
        IDomainEvent domainEvent, Type eventType, CancellationToken cancellationToken)
    {
        var handlerType = typeof(IDomainEventHandler<>).MakeGenericType(eventType);
        var handlers = services.GetServices(handlerType).ToList();

        if (handlers.Count == 0)
        {
            return;
        }

        var handle = HandleMethods.GetOrAdd(
            handlerType,
            type => type.GetMethod(nameof(IDomainEventHandler<IDomainEvent>.HandleAsync))
                ?? throw new InvalidOperationException(
                    $"{type.Name} has no HandleAsync, which should be impossible."));

        foreach (var handler in handlers)
        {
            if (handler is null)
            {
                continue;
            }

            try
            {
                await (Task)handle.Invoke(handler, [domainEvent, cancellationToken])!;
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                // Thrown before the handler's first await arrives wrapped. The
                // wrapper says nothing useful, so it is unwrapped here rather
                // than written into the outbox row.
                throw exception.InnerException;
            }
        }
    }

    private async Task<bool> FailAsync(
        OutboxMessage message, Exception exception, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = clock.Now;
        var attempts = message.Attempts + 1;

        if (attempts >= settings.MaxAttempts)
        {
            logger.LogError(
                exception,
                "Giving up on {Type} {MessageId} after {Attempts} attempts.",
                message.Type,
                message.Id,
                attempts);

            return await AbandonAsync(
                message,
                $"Failed {attempts} times. Last error: {exception.Message}",
                cancellationToken);
        }

        var delay = settings.RetryDelayAfter(attempts);

        message.MarkFailed($"{exception.GetType().Name}: {exception.Message}", now, delay);
        await database.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            exception,
            "Handling {Type} {MessageId} failed (attempt {Attempts}). Trying again in {Delay}.",
            message.Type,
            message.Id,
            attempts,
            delay);

        return true;
    }

    private async Task<bool> AbandonAsync(
        OutboxMessage message, string reason, CancellationToken cancellationToken)
    {
        message.Abandon(reason, clock.Now);
        await database.SaveChangesAsync(cancellationToken);

        logger.LogError(
            "Abandoned {Type} {MessageId}: {Reason}", message.Type, message.Id, reason);

        return true;
    }
}
