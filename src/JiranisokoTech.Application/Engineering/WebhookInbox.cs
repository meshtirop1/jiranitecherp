using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.Engineering;
using Microsoft.Extensions.Logging;

namespace JiranisokoTech.Application.Engineering;

/// <summary>What the front door decided about a request.</summary>
public enum Reception
{
    /// <summary>Signed, new, and written down.</summary>
    Accepted = 1,

    /// <summary>Signed, and already written down. Nothing to do.</summary>
    Duplicate = 2,

    /// <summary>Not signed with our secret. Not a delivery.</summary>
    Unsigned = 3,

    /// <summary>Signed, but missing something a delivery must carry.</summary>
    Malformed = 4,

    /// <summary>No secret is configured for this provider, so nothing can be trusted.</summary>
    NotConfigured = 5,
}

/// <summary>The outcome, and something a log can say.</summary>
public sealed record Receipt(Reception Outcome, string Why, Guid? DeliveryId = null);

/// <summary>
/// The front door for everything a Git host sends.
/// </summary>
/// <remarks>
/// This class does as little as it possibly can, and the restraint is the
/// design. Its whole job is: prove the request came from the provider, write the
/// body down, and return. Understanding what the body means happens later, in
/// <see cref="DeliveryDispatcher"/>, on a background loop.
///
/// The split exists because of what a provider does when a delivery is slow.
/// GitHub gives a webhook ten seconds and then records a failure and retries.
/// An inbox that parsed the payload, resolved work items, wrote commits and
/// dispatched domain events inside the request would be one slow query away
/// from timing out — and the retry that followed would do all of it again while
/// the first attempt was still running. Writing one row and returning is fast
/// enough that this never becomes a question.
///
/// It also means a bug in the handling of one event type cannot cost anything.
/// The body is already on disk before any of that code runs.
/// </remarks>
public sealed class WebhookInbox(
    IEngineeringRepository repositories,
    IEnumerable<IGitProvider> adapters,
    IWebhookSecrets secrets,
    IClock clock,
    ILogger<WebhookInbox> logger)
{
    public async Task<Receipt> ReceiveAsync(
        GitProvider provider,
        IReadOnlyDictionary<string, string> headers,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        var adapter = adapters.FirstOrDefault(one => one.Provider == provider);

        if (adapter is null)
        {
            return new Receipt(Reception.NotConfigured, $"Nothing here reads {provider}.");
        }

        if (secrets.For(provider) is not { Length: > 0 } secret)
        {
            /*
             * Refused rather than waved through. A provider with no configured
             * secret means every signature check would pass vacuously, and an
             * endpoint that accepts anything is worse than one that accepts
             * nothing: the first quietly fills the history with whatever a
             * stranger posts, and the second is noticed within the hour.
             */
            logger.LogError(
                "A {Provider} delivery arrived and no secret is configured for it, so its "
                + "signature could not be checked and it was refused.",
                provider);

            return new Receipt(
                Reception.NotConfigured,
                $"No webhook secret is configured for {provider}.");
        }

        if (!adapter.IsSigned(body.Span, adapter.SignatureIn(headers), secret))
        {
            /*
             * Logged at warning rather than error, and deliberately without the
             * body. This endpoint is on the public internet and will be probed;
             * an unsigned request is the expected background noise of being
             * reachable, not an incident, and logging what the prober sent
             * would let them write into our logs.
             */
            logger.LogWarning(
                "A request to the {Provider} webhook was not signed with our secret and was "
                + "refused.",
                provider);

            return new Receipt(Reception.Unsigned, "The signature did not match.");
        }

        /*
         * Decoded here rather than after the duplicate check, because two of the
         * four providers keep something the inbox needs in the body: Azure
         * DevOps puts the delivery identifier there, and GitLab names the event
         * inside the payload as well as in a header. The signature has already
         * been verified against the raw bytes, so decoding at this point is safe
         * — doing it any earlier would mean deciding how to trust a body from
         * the body itself.
         */
        var payload = System.Text.Encoding.UTF8.GetString(body.Span);

        if (adapter.DeliveryIdIn(headers, payload) is not { Length: > 0 } externalId)
        {
            return new Receipt(Reception.Malformed, "The delivery carried no identifier.");
        }

        if (adapter.EventIn(headers, payload) is not { Length: > 0 } eventName)
        {
            return new Receipt(Reception.Malformed, "The delivery did not say what it was.");
        }

        if (await repositories.DeliveryKnownAsync(provider, externalId, cancellationToken))
        {
            /*
             * The ordinary case, not an error. Providers retry when they do not
             * get a prompt answer, and they retry on their own schedule after
             * an outage. Answering the second copy with a plain acknowledgement
             * is what stops a queue of retries building up behind us.
             */
            return new Receipt(Reception.Duplicate, "Already received.");
        }

        var named = adapter.RepositoryIn(payload);

        var repository = named is { Length: > 0 }
            ? await repositories.ByFullNameAsync(provider, named, cancellationToken)
            : null;

        if (repository is null)
        {
            /*
             * Recorded anyway, with no repository against it. Somebody pointed
             * a webhook at this system and nothing is listening — either a
             * repository that was never connected here, or one whose name
             * changed at the provider. Both are worth seeing, and dropping the
             * delivery would make them indistinguishable from a webhook that
             * was never configured at all.
             */
            logger.LogWarning(
                "A {Provider} delivery named the repository {Repository}, which is not connected "
                + "here. It has been recorded and ignored.",
                provider,
                named ?? "(unnamed)");
        }

        var delivery = WebhookDelivery.Receive(
            provider, externalId, eventName, payload, repository?.Id, clock.Now);

        if (repository is null)
        {
            delivery.Ignored(
                $"No repository named '{named ?? "(unnamed)"}' is connected here.", clock.Now);
        }
        else
        {
            repository.Heard(clock.Now);
        }

        repositories.Add(delivery);
        await repositories.SaveAsync(cancellationToken);

        return new Receipt(Reception.Accepted, "Received.", delivery.Id);
    }
}

/// <summary>
/// The secrets deliveries are signed with.
/// </summary>
/// <remarks>
/// An interface over configuration rather than configuration itself, so that the
/// inbox can be tested without a configuration provider, and so that the one
/// place these values are read from is nameable when they eventually move into
/// a vault.
///
/// Per provider rather than per repository. Every repository at one host is
/// configured with the same secret here, which is a real limitation and an
/// honest one: per-repository secrets would mean parsing the payload to find
/// out which secret to verify it with, and parsing an unverified payload to
/// decide how to verify it is the exact inversion this endpoint exists to
/// avoid.
/// </remarks>
public interface IWebhookSecrets
{
    string? For(GitProvider provider);
}
