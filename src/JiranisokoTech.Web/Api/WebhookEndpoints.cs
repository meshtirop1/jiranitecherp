using System.Threading.RateLimiting;
using JiranisokoTech.Application.Engineering;
using JiranisokoTech.Domain.Engineering;
using Microsoft.AspNetCore.RateLimiting;

namespace JiranisokoTech.Web.Api;

/// <summary>
/// Where the Git hosts post.
/// </summary>
/// <remarks>
/// The only endpoint in this application that is both anonymous and accepts a
/// body, which makes it the one worth being careful about. Three things stand
/// in for the sign-in that is not here:
///
/// The signature. Nothing is recorded, and no payload is parsed, until the body
/// has been proved to come from somebody holding the shared secret. A caller
/// without it gets 401 and has achieved nothing.
///
/// A body limit. The request is read into memory to compute the signature over
/// it, so an unbounded body is an unauthenticated caller choosing how much
/// memory this process uses.
///
/// A rate limit. GitHub sends a burst after an outage and that is fine; what is
/// not fine is somebody discovering the address and posting to it continuously,
/// because each attempt costs an HMAC over whatever they sent.
///
/// It returns 200 for a delivery it has merely written down and not yet
/// understood, and that is correct rather than lazy. The provider is asking
/// "did you receive this?", not "did you agree with it" — and a non-2xx answer
/// makes GitHub retry a delivery that is already safely on disk.
/// </remarks>
public static class WebhookEndpoints
{
    public const string Policy = "webhooks";

    /// <summary>
    /// The most a delivery may be.
    /// </summary>
    /// <remarks>
    /// Four megabytes. A push of a hundred commits with long messages runs to a
    /// few hundred kilobytes, so this is roughly ten times the largest real
    /// payload — generous enough never to refuse a genuine delivery, small
    /// enough that a stranger cannot choose this process's memory usage.
    /// </remarks>
    private const int MaximumBody = 4 * 1024 * 1024;

    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var hooks = endpoints.MapGroup("/webhooks")
            .AllowAnonymous()
            .RequireRateLimiting(Policy)
            .ExcludeFromDescription();

        /*
         * A route per provider rather than one route that works out which it is.
         * The provider decides which secret to verify against and which adapter
         * reads the body, and guessing that from the payload would mean reading an
         * unverified body to decide how to verify it — the exact inversion this
         * endpoint exists to avoid.
         */
        foreach (var provider in Enum.GetValues<GitProvider>())
        {
            var path = "/" + provider.ToString().ToLowerInvariant();

            hooks.MapPost(
                path,
                (HttpContext context, WebhookInbox inbox, CancellationToken token)
                    => ReceiveAsync(provider, context, inbox, token));
        }

        return endpoints;
    }

    private static async Task<IResult> ReceiveAsync(
        GitProvider provider,
        HttpContext context,
        WebhookInbox inbox,
        CancellationToken cancellationToken)
    {
        if (await ReadAsync(context.Request.Body, cancellationToken) is not { } body)
        {
            return Refused(
                StatusCodes.Status413PayloadTooLarge, "That delivery was too large.");
        }

        var headers = context.Request.Headers
            .ToDictionary(
                header => header.Key,
                header => header.Value.ToString(),
                StringComparer.OrdinalIgnoreCase);

        var receipt = await inbox.ReceiveAsync(provider, headers, body, cancellationToken);

        return receipt.Outcome switch
        {
            Reception.Unsigned => Refused(
                StatusCodes.Status401Unauthorized, "The signature did not match."),

            /*
             * 400 rather than 500. A signed request missing the delivery
             * identifier is a correctly authenticated caller sending something
             * malformed, which is the definition of a bad request — and
             * answering 500 would make a provider retry it forever.
             */
            Reception.Malformed => Refused(StatusCodes.Status400BadRequest, receipt.Why),

            /*
             * 503, and the status matters more here than anywhere else on this
             * endpoint: a provider retries a 5xx and gives up permanently on a
             * 4xx. A missing secret is this system's own misconfiguration and
             * must therefore be survivable, or every delivery that arrives
             * before somebody notices is gone for good.
             *
             * Vague on purpose. The caller is unauthenticated; the log and the
             * repositories screen carry the detail.
             */
            Reception.NotConfigured => Refused(
                StatusCodes.Status503ServiceUnavailable,
                "Deliveries cannot be accepted just now."),

            _ => Results.Ok(new { received = true }),
        };
    }

    /// <summary>
    /// A refusal, always with a body.
    /// </summary>
    /// <remarks>
    /// The body is not decoration. UseStatusCodePagesWithReExecute replays any
    /// response that has a failing status and no body at all, and on a POST that
    /// replay is refused by the antiforgery middleware — so an empty 401, 413 or
    /// 503 from here reached the provider as 400. Since a provider retries a 5xx
    /// and gives up on a 4xx, that turned a recoverable misconfiguration into
    /// deliveries lost for good.
    ///
    /// It also earns its keep at the far end: GitHub shows this body in its own
    /// delivery log, which is where somebody setting a webhook up is looking.
    /// </remarks>
    private static IResult Refused(int status, string why) =>
        Results.Json(new { error = why }, statusCode: status);

    /// <summary>
    /// The whole body, or nothing if it is too big.
    /// </summary>
    /// <remarks>
    /// Read into memory deliberately: the signature is computed over the exact
    /// bytes, so they all have to be in hand at once. The limit is enforced
    /// while reading rather than by checking Content-Length first, because
    /// Content-Length is supplied by the caller and a chunked request does not
    /// carry one at all.
    /// </remarks>
    private static async Task<ReadOnlyMemory<byte>?> ReadAsync(
        Stream body, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];

        while (true)
        {
            var read = await body.ReadAsync(chunk, cancellationToken);

            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaximumBody)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// How often one address may post here.
    /// </summary>
    /// <remarks>
    /// By address, unlike the API's limit, because there is no key to partition
    /// on — an unauthenticated caller has told us nothing else about itself.
    /// GitHub posts from a published set of addresses and a real repository
    /// produces a handful of deliveries a minute even during a busy merge, so a
    /// bucket that refills at sixty a minute is far above anything genuine
    /// while still bounding what a stranger can spend of ours.
    /// </remarks>
    public static RateLimiterOptions AddWebhookLimits(this RateLimiterOptions limiter)
    {
        limiter.AddPolicy(Policy, context => RateLimitPartition.GetTokenBucketLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 300,
                TokensPerPeriod = 60,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

        return limiter;
    }
}
