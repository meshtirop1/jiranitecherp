using System.Security.Cryptography;
using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.Integrations;

namespace JiranisokoTech.Application.Integrations;

/// <summary>What somebody does to an outgoing webhook.</summary>
public sealed class SubscriptionService(
    IIntegrationRepository integrations,
    ISecretStore secrets,
    IClock clock)
{
    /// <summary>
    /// Add a subscription, and hand back the secret once.
    /// </summary>
    /// <remarks>
    /// The secret is generated here rather than typed, which is the opposite of the
    /// incoming case and right for the opposite reason. An incoming secret has to
    /// match whatever GitHub was configured with, so a person must be able to supply
    /// it. An outgoing one is ours to choose, and a person choosing it would choose
    /// something weaker than thirty-two random bytes.
    ///
    /// Returned in the clear exactly once, for pasting into the receiving system. It
    /// is stored encrypted, so it can be read back to sign with — but showing it again
    /// on a screen would put it in a browser's history and a support screenshot, and
    /// there is no reason to.
    /// </remarks>
    public async Task<(Subscription Subscription, string Secret)> AddAsync(
        string name,
        string endpoint,
        IEnumerable<string> events,
        CancellationToken cancellationToken = default)
    {
        var wanted = events.ToList();

        if (wanted.FirstOrDefault(one => !OutboundEvents.IsOffered(one)) is { } unknown)
        {
            throw new InvalidOperationException(
                $"'{unknown}' is not an event anything can subscribe to. Only the events on the "
                + "offered list leave this system, and that list is deliberately short.");
        }

        if (await integrations.EndpointTakenAsync(endpoint.Trim(), cancellationToken))
        {
            /*
             * Refused rather than allowed twice. Two subscriptions to one address is
             * almost always somebody adding what they thought was missing, and the
             * result is a receiver told everything twice — which for an integration
             * that acts on notifications means acting twice.
             */
            throw new InvalidOperationException(
                "There is already a subscription pointing at that address. Change the one that "
                + "is there rather than adding a second, or the receiver is told everything "
                + "twice.");
        }

        var secret = NewSecret();

        var subscription = Subscription.Add(
            name, endpoint, secrets.Protect(secret), wanted, clock.Now);

        integrations.Add(subscription);
        await integrations.SaveAsync(cancellationToken);

        return (subscription, secret);
    }

    public async Task RenameAsync(
        Guid id, string name, CancellationToken cancellationToken = default)
    {
        var subscription = await Required(id, cancellationToken);

        subscription.Rename(name);
        await integrations.SaveAsync(cancellationToken);
    }

    public async Task DisableAsync(
        Guid id, string reason, CancellationToken cancellationToken = default)
    {
        var subscription = await Required(id, cancellationToken);

        subscription.Disable(reason, clock.Now);
        await integrations.SaveAsync(cancellationToken);
    }

    public async Task EnableAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var subscription = await Required(id, cancellationToken);

        subscription.Enable(clock.Now);
        await integrations.SaveAsync(cancellationToken);
    }

    /// <summary>Put a notification that gave up back in the queue.</summary>
    public async Task RetryAsync(Guid deliveryId, CancellationToken cancellationToken = default)
    {
        var delivery = await integrations.FindDeliveryAsync(deliveryId, cancellationToken)
            ?? throw new InvalidOperationException("That notification is not recorded.");

        delivery.Retry(clock.Now);
        await integrations.SaveAsync(cancellationToken);
    }

    private async Task<Subscription> Required(Guid id, CancellationToken cancellationToken) =>
        await integrations.FindAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("There is no such subscription.");

    /// <summary>
    /// Thirty-two random bytes, hex.
    /// </summary>
    /// <remarks>
    /// Hex rather than base64 because the receiving end is somebody else's code, and
    /// hex has no characters that a URL, a shell or a YAML file will treat as
    /// punctuation. The strength is the same and the number of support conversations
    /// is not.
    /// </remarks>
    private static string NewSecret() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
}
