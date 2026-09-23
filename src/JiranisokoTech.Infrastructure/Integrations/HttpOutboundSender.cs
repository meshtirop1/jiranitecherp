using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using JiranisokoTech.Application.Integrations;
using JiranisokoTech.Domain.Integrations;

namespace JiranisokoTech.Infrastructure.Integrations;

/// <summary>
/// Posts a signed notification to somebody else's server.
/// </summary>
/// <remarks>
/// The one place in this system that makes an outbound request to an address a user
/// supplied, which makes it the one place worth being careful about in the opposite
/// direction from everything else.
///
/// The signature is the same scheme this system accepts from GitHub — HMAC-SHA256 over
/// the exact body bytes, hex, behind a `sha256=` prefix — deliberately, so that
/// whoever receives it can use any of the many libraries written for GitHub webhooks
/// without being told anything new.
/// </remarks>
public sealed class HttpOutboundSender(IHttpClientFactory clients) : IOutboundSender
{
    public const string ClientName = "outbound-webhooks";

    /// <summary>The header carrying the signature.</summary>
    /// <remarks>
    /// Prefixed with the firm's own name rather than reusing `X-Hub-Signature-256`.
    /// Borrowing GitHub's header name would tell a receiver that this is GitHub, and
    /// something downstream would eventually treat it as such.
    /// </remarks>
    public const string SignatureHeader = "X-Jiranisoko-Signature-256";

    public const string EventHeader = "X-Jiranisoko-Event";

    public const string DeliveryHeader = "X-Jiranisoko-Delivery";

    public async Task<SendResult> SendAsync(
        Subscription subscription,
        OutboundDelivery delivery,
        string secret,
        CancellationToken cancellationToken = default)
    {
        var body = Encoding.UTF8.GetBytes(delivery.Payload);

        using var request = new HttpRequestMessage(HttpMethod.Post, subscription.Endpoint)
        {
            Content = new ByteArrayContent(body),
        };

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };

        request.Headers.TryAddWithoutValidation(
            SignatureHeader,
            "sha256=" + Convert.ToHexStringLower(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)));

        request.Headers.TryAddWithoutValidation(EventHeader, delivery.Event);

        /*
         * The delivery's own identifier, so a receiver can be idempotent the way this
         * system is about incoming deliveries. Every retry of this notification carries
         * the same value — that is the point, and it is why the identifier is the row's
         * rather than newly generated per attempt.
         */
        request.Headers.TryAddWithoutValidation(DeliveryHeader, delivery.Id.ToString());

        try
        {
            using var client = clients.CreateClient(ClientName);
            using var response = await client.SendAsync(request, cancellationToken);

            var code = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                return SendResult.Ok(code);
            }

            /*
             * A 4xx is still retried, with one exception. The tempting rule is "4xx
             * means we are wrong, so give up" — but the commonest 4xx from a webhook
             * receiver is 401 or 403 while somebody is still wiring up the secret at
             * their end, and 404 while the route is being deployed. Both pass. 410
             * Gone is the one status that means what it says, and a receiver taking
             * the trouble to send it has earned being believed.
             */
            return response.StatusCode == HttpStatusCode.Gone
                ? SendResult.Refused(code, "The endpoint says it is gone for good (410).")
                : SendResult.Refused(code, $"The endpoint answered {code}.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A timeout, not a shutdown. Said in those words because "the operation
            // was canceled" on a screen tells whoever reads it nothing.
            return SendResult.Unreachable("The endpoint did not answer in time.");
        }
        catch (HttpRequestException exception)
        {
            return SendResult.Unreachable(exception.Message);
        }
    }
}
