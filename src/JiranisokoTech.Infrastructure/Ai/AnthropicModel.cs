using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JiranisokoTech.Application.Ai;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JiranisokoTech.Infrastructure.Ai;

/// <summary>
/// Anthropic's Messages API, over plain HTTP.
/// </summary>
/// <remarks>
/// Plain HTTP through <see cref="IHttpClientFactory"/> rather than the provider's SDK, so the one
/// outbound dependency this feature has is the same kind as the webhook sender's and needs no new
/// package in the image. The request is small enough to write out: a model, the instructions, the
/// conversation, the lookups on offer, and three settings.
///
/// Three things in it are not obvious and each prevents a specific failure.
///
/// The previous reply is handed back exactly as it arrived (<see cref="Replied.Raw"/>). The model
/// thinks before it answers and returns that thinking in blocks that are only valid unchanged; a
/// client that rebuilt the reply from the text it understood would drop them, and the provider
/// refuses a conversation whose earlier turns have been edited.
///
/// The instructions are marked cacheable and never carry anything that changes between calls —
/// not the date, not the person's name. A question that makes four lookups resends the same
/// instructions four times, and a cached prefix is billed at a fraction of a fresh one only if it
/// is byte-identical.
///
/// Refusals are opted into falling back to another model on the provider's side
/// (<c>fallbacks: "default"</c>). A declined request otherwise comes back as a successful reply
/// with nothing in it, and a legitimate question about a failed deployment is exactly the kind of
/// text a safety classifier occasionally misreads.
/// </remarks>
public sealed class AnthropicModel(
    IHttpClientFactory clients,
    IOptions<AiOptions> options,
    ILogger<AnthropicModel> logger) : IAiModel
{
    public const string ClientName = "ai";

    private const string ApiVersion = "2023-06-01";

    private const string FallbackBeta = "server-side-fallback-2026-07-01";

    /// <summary>Tries in all, the first included.</summary>
    private const int Attempts = 3;

    private readonly AiOptions _options = options.Value;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public string Name => _options.Model;

    public async Task<ModelReply> AskAsync(
        ModelRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            throw new AiUnavailableException(
                AiFailure.NotConfigured,
                "The assistant is not configured: no API key has been set for it.");
        }

        var body = Body(request).ToJsonString();

        /*
         * One deadline across every attempt, rather than a timeout per attempt. Three attempts of
         * two minutes each is six minutes of somebody watching a page, and the person does not care
         * which attempt was slow.
         */
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.Timeout);

        for (var attempt = 1; ; attempt++)
        {
            Answer answer;

            try
            {
                answer = await SendAsync(body, deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AiUnavailableException(
                    AiFailure.TimedOut,
                    $"No answer came back within {Said(_options.Timeout)}. Nothing was changed; "
                    + "try a narrower question.");
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning(exception, "The AI provider could not be reached.");

                throw new AiUnavailableException(
                    AiFailure.Unreachable,
                    "The AI provider could not be reached from this server.",
                    exception);
            }

            if (answer.Status is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices)
            {
                return Read(answer.Text);
            }

            var failure = Classify(answer.Status);

            if (Retryable(failure) && attempt < Attempts)
            {
                var wait = Wait(answer.RetryAfter, attempt);

                logger.LogInformation(
                    "The AI provider answered {Status}; trying again in {Wait}.",
                    (int)answer.Status, wait);

                try
                {
                    await Task.Delay(wait, deadline.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new AiUnavailableException(
                        AiFailure.TimedOut,
                        $"No answer came back within {Said(_options.Timeout)}; the provider "
                        + "was busy and the retries ran out of time.");
                }

                continue;
            }

            logger.LogWarning(
                "The AI provider refused a request with {Status}: {Body}",
                (int)answer.Status, Truncate(answer.Text, 500));

            throw new AiUnavailableException(failure, Explain(failure, answer.Status, answer.Text));
        }
    }

    /// <summary>What came back, read in full inside the deadline.</summary>
    private sealed record Answer(HttpStatusCode Status, TimeSpan? RetryAfter, string Text);

    private async Task<Answer> SendAsync(string body, CancellationToken cancellationToken)
    {
        var client = clients.CreateClient(ClientName);

        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        message.Headers.TryAddWithoutValidation("x-api-key", _options.ApiKey);
        message.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        message.Headers.TryAddWithoutValidation("anthropic-beta", FallbackBeta);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.SendAsync(message, cancellationToken);

        return new Answer(
            response.StatusCode,
            response.Headers.RetryAfter?.Delta,
            await response.Content.ReadAsStringAsync(cancellationToken));
    }

    /// <summary>The request, written out.</summary>
    /// <remarks>
    /// No <c>thinking</c> field and no sampling settings. This model always thinks and refuses a
    /// request that tries to turn it off or to set a temperature; effort is the one control, and
    /// it is set explicitly rather than left to a default that changes between models.
    ///
    /// No forced tool choice either, for the same reason: the model decides whether to look
    /// something up, and the instructions tell it that it should.
    /// </remarks>
    internal JsonObject Body(ModelRequest request)
    {
        var output = new JsonObject { ["effort"] = _options.Effort };

        if (request.ReplySchema is { } schema)
        {
            output["format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["schema"] = JsonNode.Parse(schema),
            };
        }

        var body = new JsonObject
        {
            ["model"] = _options.Model,
            ["max_tokens"] = _options.MaxTokens,
            ["system"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = request.Instructions,
                ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
            }),
            ["messages"] = new JsonArray([.. request.Conversation.Select(Message)]),
            ["output_config"] = output,
            ["fallbacks"] = "default",
        };

        if (request.Tools.Count > 0)
        {
            body["tools"] = new JsonArray([.. request.Tools.Select(tool => (JsonNode)new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = JsonNode.Parse(tool.InputSchema),
            })]);
        }

        return body;
    }

    private static JsonNode Message(ModelTurn turn) => turn switch
    {
        Asked asked => new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(
            [
                // Documents before the question, which is where the provider reads them best.
                .. (asked.Enclosures ?? []).Select(enclosure => (JsonNode)new JsonObject
                {
                    ["type"] = "document",
                    ["title"] = enclosure.Title,
                    ["source"] = new JsonObject
                    {
                        ["type"] = "base64",
                        ["media_type"] = enclosure.MediaType,
                        ["data"] = Convert.ToBase64String(enclosure.Data),
                    },
                }),
                new JsonObject { ["type"] = "text", ["text"] = asked.Text },
            ]),
        },

        // Verbatim. See the remarks on the class for why nothing here may be rebuilt.
        Replied replied => new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = JsonNode.Parse(replied.Raw),
        },

        ToolResults results => new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray([.. results.Results.Select(result => (JsonNode)new JsonObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = result.CallId,
                ["content"] = result.Content,
                ["is_error"] = result.IsError,
            })]),
        },

        _ => throw new ArgumentOutOfRangeException(nameof(turn), turn.GetType().Name, null),
    };

    /// <summary>
    /// The reply, read by block type rather than position.
    /// </summary>
    /// <remarks>
    /// A reply can open with thinking blocks, carry a marker where it fell back to another model,
    /// and only then have its text. Code that read the first block as the answer would show an
    /// empty string on most replies and the right one on a few.
    /// </remarks>
    internal static ModelReply Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var content = root.TryGetProperty("content", out var blocks) && blocks.ValueKind == JsonValueKind.Array
            ? blocks
            : default;

        var text = new StringBuilder();
        var calls = new List<ToolCall>();

        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                switch (block.GetProperty("type").GetString())
                {
                    case "text":
                        text.Append(block.GetProperty("text").GetString());
                        break;

                    case "tool_use":
                        calls.Add(new ToolCall(
                            block.GetProperty("id").GetString()!,
                            block.GetProperty("name").GetString()!,
                            block.GetProperty("input").GetRawText()));
                        break;
                }
            }
        }

        var end = (root.TryGetProperty("stop_reason", out var stop) ? stop.GetString() : null) switch
        {
            "tool_use" => ReplyEnd.WantsTools,
            "max_tokens" => ReplyEnd.Truncated,
            "refusal" => ReplyEnd.Declined,
            _ => ReplyEnd.Finished,
        };

        var usage = root.TryGetProperty("usage", out var used) ? used : default;

        return new ModelReply(
            end,
            text.ToString(),
            calls,
            content.ValueKind == JsonValueKind.Array ? content.GetRawText() : "[]",
            Tokens(usage, "input_tokens") + Tokens(usage, "cache_read_input_tokens")
                + Tokens(usage, "cache_creation_input_tokens"),
            Tokens(usage, "output_tokens"),
            root.TryGetProperty("model", out var model) ? model.GetString() ?? string.Empty : string.Empty);
    }

    private static int Tokens(JsonElement usage, string name) =>
        usage.ValueKind == JsonValueKind.Object
        && usage.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    internal static AiFailure Classify(HttpStatusCode status) => (int)status switch
    {
        429 => AiFailure.RateLimited,
        529 => AiFailure.Overloaded,
        >= 500 => AiFailure.ProviderError,
        _ => AiFailure.Rejected,
    };

    private static bool Retryable(AiFailure failure) =>
        failure is AiFailure.RateLimited or AiFailure.Overloaded or AiFailure.ProviderError;

    /// <summary>
    /// How long to wait before trying again.
    /// </summary>
    /// <remarks>
    /// The provider's own figure when it gives one, capped, because a retry-after of a minute is
    /// the provider saying "not now" and a person is waiting on the page — better to tell them
    /// than to hold the request open for most of the deadline.
    /// </remarks>
    private static TimeSpan Wait(TimeSpan? retryAfter, int attempt)
    {
        if (retryAfter is { } delta)
        {
            return delta > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : delta;
        }

        return TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
    }

    internal static string Explain(AiFailure failure, HttpStatusCode status, string body) => failure switch
    {
        AiFailure.RateLimited =>
            "The AI provider is limiting how often this firm may ask. Try again in a minute.",
        AiFailure.Overloaded =>
            "The AI provider is overloaded at the moment. Try again in a few minutes.",
        AiFailure.ProviderError =>
            $"The AI provider failed on its side ({(int)status}). Try again shortly.",
        _ => (int)status switch
        {
            401 or 403 =>
                "The AI provider refused this server's API key. Whoever looks after the "
                + "configuration needs to check Ai:ApiKey.",
            404 =>
                "The AI provider does not offer the configured model to this key. Whoever looks "
                + "after the configuration needs to check Ai:Model.",
            413 => "That was too much to send in one request. Try a narrower question.",
            _ => $"The AI provider refused the request ({(int)status}): {ProviderMessage(body)}",
        },
    };

    private static string ProviderMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var message))
            {
                return Truncate(message.GetString() ?? string.Empty, 300);
            }
        }
        catch (JsonException)
        {
            // Not JSON — a proxy's error page, usually. Say so rather than print HTML.
        }

        return "no reason given.";
    }

    private static string Truncate(string text, int length) =>
        text.Length <= length ? text : text[..length] + "…";

    private static string Said(TimeSpan span) => span.TotalSeconds < 120
        ? $"{(int)span.TotalSeconds} seconds"
        : $"{(int)span.TotalMinutes} minutes";
}
