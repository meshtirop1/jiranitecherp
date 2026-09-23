using JiranisokoTech.Application.Integrations;
using JiranisokoTech.Domain.Integrations;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Integrations;

public sealed class IntegrationRepository(AppDbContext context) : IIntegrationRepository
{
    public Task<Subscription?> FindAsync(
        Guid id, CancellationToken cancellationToken = default) =>
        context.Subscriptions.FirstOrDefaultAsync(one => one.Id == id, cancellationToken);

    public Task<List<Subscription>> AllAsync(CancellationToken cancellationToken = default) =>
        context.Subscriptions
            .OrderBy(one => one.DisabledAt == null ? 0 : 1)
            .ThenBy(one => one.Name)
            .ToListAsync(cancellationToken);

    public Task<List<Subscription>> WantingAsync(
        string eventName, CancellationToken cancellationToken = default) =>
        context.Subscriptions
            .Where(one => one.DisabledAt == null
                && one.Wanted.Any(wanted => wanted.Name == eventName))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Is something already pointing at this address?
    /// </summary>
    /// <remarks>
    /// Compared without regard to case, and only against subscriptions still switched
    /// on. A host name is case-insensitive by definition, and refusing an address that
    /// only an old disabled subscription holds would leave somebody unable to re-add
    /// the integration they had just switched off.
    /// </remarks>
    public Task<bool> EndpointTakenAsync(
        string endpoint, CancellationToken cancellationToken = default) =>
        context.Subscriptions.AnyAsync(
            one => one.DisabledAt == null && one.Endpoint.ToLower() == endpoint.ToLower(),
            cancellationToken);

    public Task<OutboundDelivery?> FindDeliveryAsync(
        Guid id, CancellationToken cancellationToken = default) =>
        context.OutboundDeliveries.FirstOrDefaultAsync(one => one.Id == id, cancellationToken);

    public Task<List<OutboundDelivery>> DueAsync(
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken = default) =>
        context.OutboundDeliveries
            .Where(one =>
                (one.Status == OutboundStatus.Waiting || one.Status == OutboundStatus.Failed)
                && (one.NextAttemptAt == null || one.NextAttemptAt <= now))
            .OrderBy(one => one.QueuedAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

    public void Add(Subscription subscription) => context.Subscriptions.Add(subscription);

    public void Add(OutboundDelivery delivery) => context.OutboundDeliveries.Add(delivery);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        context.SaveChangesAsync(cancellationToken);
}

/// <summary>
/// Secrets encrypted with the application's own key ring.
/// </summary>
/// <remarks>
/// The key ring lives on a volume outside the container — see SigningKeys — so a copy
/// of the database on its own yields nothing. That is the whole security argument for
/// storing a recoverable secret at all, and it holds only as long as the two are kept
/// apart: a backup that takes the database and the keys together undoes it.
///
/// The purpose string keeps these separate from cookies and Identity tokens, which
/// share the same key ring. Without it a value protected for one use could be
/// presented as another, and a bug somewhere else would become a way to forge these.
/// </remarks>
public sealed class DataProtectionSecretStore : ISecretStore
{
    private readonly IDataProtector _protector;

    public DataProtectionSecretStore(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector("JiranisokoTech.Integrations.WebhookSecret.v1");

    public string Protect(string plain) => _protector.Protect(plain);

    public string? Reveal(string protectedValue)
    {
        try
        {
            return _protector.Unprotect(protectedValue);
        }
        catch (Exception)
        {
            /*
             * Swallowed on purpose, and this is the one place in the codebase where a
             * bare catch is right. Unprotect throws for every reason from a lost key
             * ring to a truncated column, none of them distinguishable and none of
             * them recoverable here. The caller treats nothing as "this subscription
             * is broken, switch it off and tell somebody", which is the only useful
             * response to any of them.
             */
            return null;
        }
    }
}
