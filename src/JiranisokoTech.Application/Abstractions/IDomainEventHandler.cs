using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Application.Abstractions;

/// <summary>
/// Something that reacts to a fact after it has happened.
/// </summary>
/// <remarks>
/// Handlers run from the outbox, after the transaction that raised the event has
/// committed. Three consequences follow, and all three are requirements rather
/// than advice:
///
/// A handler must be idempotent. Delivery is at least once, not exactly once: a
/// process can dispatch a message, send the email, and die before it records
/// that it did. The message is then delivered again, and the only defence is a
/// handler that can be run twice without doing the thing twice.
///
/// A handler must not assume it is the only one. Several can subscribe to one
/// event, they run in no particular order, and if a later one throws the whole
/// message is retried — which means the earlier ones run again.
///
/// A handler receives values, never entities. What arrives is JSON rebuilt into
/// the event record; anything it needs beyond that, it loads for itself.
/// </remarks>
public interface IDomainEventHandler<in TEvent> where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken = default);
}
