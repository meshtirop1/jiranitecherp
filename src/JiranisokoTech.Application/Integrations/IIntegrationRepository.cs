using JiranisokoTech.Domain.Integrations;

namespace JiranisokoTech.Application.Integrations;

public interface IIntegrationRepository
{
    Task<Subscription?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    Task<List<Subscription>> AllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The active subscriptions that asked for this event.
    /// </summary>
    /// <remarks>
    /// Filtered in the database rather than by loading them all and asking each. A
    /// firm with forty subscriptions raises thousands of events a day, and the
    /// alternative is forty rows and their event lists read on every one.
    /// </remarks>
    Task<List<Subscription>> WantingAsync(
        string eventName, CancellationToken cancellationToken = default);

    Task<bool> EndpointTakenAsync(
        string endpoint, CancellationToken cancellationToken = default);

    Task<OutboundDelivery?> FindDeliveryAsync(
        Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Notifications due to be sent, oldest first.
    /// </summary>
    /// <remarks>
    /// Due, not merely waiting: a failed delivery carries the time it may next be
    /// tried, and ignoring that would turn the backoff into a tight loop against an
    /// endpoint that has already said no.
    /// </remarks>
    Task<List<OutboundDelivery>> DueAsync(
        DateTimeOffset now, int batchSize, CancellationToken cancellationToken = default);

    void Add(Subscription subscription);

    void Add(OutboundDelivery delivery);

    Task SaveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Keeping a secret that has to be read back.
/// </summary>
/// <remarks>
/// Almost everything secret in this system is hashed, which is better because it
/// cannot be undone. An outgoing webhook secret is the exception: the payload has to
/// be signed on the way out and a signature cannot be computed from a hash.
///
/// So it is encrypted with the application's own key ring, which lives on a volume
/// rather than in the database — a copy of the database alone therefore yields
/// nothing. An interface, so that the one place this happens has a name, and so that
/// moving these into a vault later is a second implementation rather than a search.
/// </remarks>
public interface ISecretStore
{
    string Protect(string plain);

    /// <summary>
    /// The original value, or nothing if it cannot be read back.
    /// </summary>
    /// <remarks>
    /// Nothing rather than an exception, because the realistic cause is a key ring
    /// that was lost or replaced — and that must show up as one clearly broken
    /// subscription somebody can re-enter, not as a background loop throwing on every
    /// pass.
    /// </remarks>
    string? Reveal(string protectedValue);
}
