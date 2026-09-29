using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JiranisokoTech.Application.Integrations;
using JiranisokoTech.Domain.Integrations;

namespace JiranisokoTech.Infrastructure.Integrations;

/// <summary>
/// Posts a sentence into a Slack channel.
/// </summary>
/// <remarks>
/// Section 51, and the second provider on the same machinery — which is the point rather than a
/// convenience. Everything that decides whether to notify somebody is untouched: the domain
/// events, the offered list, the queue, the retries, the backoff, the dead letters and the replay
/// button all behave identically. What a provider changes is the bytes on the wire, and that is
/// this file.
///
/// <b>The reason it is worth building is that the outbound half of section 40 had no possible
/// consumer.</b> It posts a bespoke envelope that only code somebody writes can read, and a firm
/// of twenty software engineers has not written that code — so every retry, every dead letter and
/// every replay button was machinery with nothing at the far end.
///
/// <b>Nothing is signed, and that is Slack's arrangement rather than a corner cut.</b> A Slack
/// incoming webhook has no shared secret to sign with: the URL itself is the credential, which is
/// why one must never be pasted anywhere but into this form and why the endpoint column is the
/// most sensitive thing in this table for a destination of this kind. The subscription still
/// carries a generated secret because the column is not nullable; it is not sent.
/// </remarks>
public sealed class SlackOutboundSender(IHttpClientFactory clients) : IOutboundSender
{
    public const string ClientName = "slack-webhooks";

    /// <summary>
    /// The longest message posted, with room to spare.
    /// </summary>
    /// <remarks>
    /// A ticket's subject runs to three hundred characters and a client's name to two hundred, so
    /// nothing here is near Slack's own limit. The clip exists so that the day somebody adds an
    /// event carrying a paragraph, a channel gets a long message rather than a refusal it has to
    /// be told about — a notification truncated is read, and one rejected is not.
    /// </remarks>
    private const int Longest = 2_000;

    /// <summary>
    /// The fields worth naming a notification by, in the order they are looked for.
    /// </summary>
    /// <remarks>
    /// Read out of the payload rather than written as fourteen templates against fourteen event
    /// shapes. Fourteen templates is fourteen places where renaming a field on a domain event —
    /// an ordinary thing to do — turns the channel message into "An invoice was sent to a client.
    /// KES" with a gap in it, silently, where nobody who reads the code ever looks.
    ///
    /// So the sentence comes from <see cref="OutboundEvents.Offered"/>, which somebody already
    /// wrote and which the screen already shows beside the checkbox, and the identifying line is
    /// whichever of these the event happens to carry. An event carrying none of them still posts
    /// its sentence and its name, which is the graceful end of the degradation rather than the
    /// silent one.
    /// </remarks>
    private static readonly string[] WorthNaming =
        ["reference", "number", "title", "subject", "fullName", "name", "code"];

    public DestinationKind Handles => DestinationKind.Slack;

    public async Task<SendResult> SendAsync(
        Subscription subscription,
        OutboundDelivery delivery,
        string secret,
        CancellationToken cancellationToken = default)
    {
        var text = Say(delivery);

        var body = JsonSerializer.SerializeToUtf8Bytes(new SlackMessage(text));

        using var request = new HttpRequestMessage(HttpMethod.Post, subscription.Endpoint)
        {
            Content = new ByteArrayContent(body),
        };

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };

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
             * A 404 is terminal here, and it is NOT in HttpOutboundSender. The difference is real:
             * for somebody's own server a 404 is usually a route still being deployed, so it is
             * worth retrying; for a Slack incoming webhook the path IS the credential, so a 404
             * means the hook was deleted or revoked, and no amount of waiting brings it back.
             * Retrying would spend days of attempts and then disable the subscription with
             * "the endpoint answered 404", which tells whoever reads it nothing about what to do.
             */
            return response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone
                ? SendResult.Refused(
                    code,
                    $"Slack says that webhook no longer exists ({code}). It was most likely "
                    + "revoked or the app was removed from the workspace — add a new incoming "
                    + "webhook and a new subscription for it.")
                : SendResult.Refused(code, $"Slack answered {code}.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SendResult.Unreachable("Slack did not answer in time.");
        }
        catch (HttpRequestException exception)
        {
            return SendResult.Unreachable(exception.Message);
        }
    }

    /// <summary>
    /// What this notification reads as in a channel.
    /// </summary>
    /// <remarks>
    /// Public and static so it can be tested without a network, which matters more here than
    /// anywhere else in this file: the wording is the whole feature, and a test of it that had to
    /// stand up an HTTP handler would be a test nobody writes a second case for.
    /// </remarks>
    public static string Say(OutboundDelivery delivery)
    {
        var said = OutboundEvents.Offered.TryGetValue(delivery.Event, out var description)
            ? description
            : $"{delivery.Event} happened.";

        var naming = Naming(delivery.Payload);

        var text = naming is null
            ? $"{said}  ({delivery.Event})"
            : $"{said}  {naming}  ({delivery.Event})";

        return text.Length > Longest ? text[..Longest] : text;
    }

    /// <summary>
    /// Which thing it was about, out of the payload, or nothing.
    /// </summary>
    /// <remarks>
    /// Parsed defensively and answering null on anything unexpected. This runs against a string
    /// that was serialised by this application, so malformed JSON is close to impossible — but the
    /// row may have been written by a version of the code that is no longer here, a replay button
    /// exists, and a thrown exception in here would turn a channel notification into a delivery
    /// that retries for two days and dead-letters. A missing name is worth losing; the message is
    /// not.
    /// </remarks>
    private static string? Naming(string payload)
    {
        try
        {
            if (JsonNode.Parse(payload) is not JsonObject envelope
                || envelope["data"] is not JsonObject data)
            {
                return null;
            }

            var parts = new List<string>();

            foreach (var field in WorthNaming)
            {
                if (data[field] is JsonValue value
                    && value.ToString() is { Length: > 0 } said
                    && !parts.Contains(said, StringComparer.Ordinal))
                {
                    parts.Add(said);
                }

                /*
                 * Two at most. The point of this line is to let somebody reading the channel know
                 * which invoice or which candidate, not to reproduce the payload in a chat
                 * message — and a notification that fills six lines is one people mute.
                 */
                if (parts.Count == 2)
                {
                    break;
                }
            }

            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// What Slack wants.
    /// </summary>
    /// <remarks>
    /// One field. Slack's blocks would let this be laid out with a heading and a link, and it is
    /// deliberately not used: blocks are a schema that has changed under people before, and a
    /// message that fails to render is worse than a plain one. The event's own words are the
    /// content, and they read the same in every client.
    /// </remarks>
    private sealed record SlackMessage(string Text)
    {
        [System.Text.Json.Serialization.JsonPropertyName("text")]
        public string Text { get; init; } = Text;
    }
}
