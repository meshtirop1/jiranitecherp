using System.Net;
using System.Text;
using System.Text.Json;
using JiranisokoTech.Application.Ai;
using JiranisokoTech.Infrastructure.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JiranisokoTech.Tests.Ai;

/// <summary>
/// The HTTP client for the provider, against a handler standing in for the network.
/// </summary>
/// <remarks>
/// Nothing here reaches the provider. What is checked is the part that is ours: the request this
/// code writes, how it reads what comes back, and what it does when the answer is a refusal, a
/// rate limit, an outage or silence — each of which a person on a page has to be told about in
/// words, and some of which are worth one more try.
/// </remarks>
public class AnthropicModelTests
{
    private static readonly ModelRequest Question = new(
        "Answer from the records.",
        [new Asked("Which projects are late?")],
        [new ToolDefinition("list_projects", "List projects.", """{"type":"object","properties":{},"additionalProperties":false}""")]);

    [Fact]
    public async Task The_request_names_the_model_effort_and_fallback_and_nothing_this_model_refuses()
    {
        var network = new Network().Answer(HttpStatusCode.OK, Finished("On time."));

        await Model(network).AskAsync(Question);

        var sent = Assert.Single(network.Sent);
        using var body = JsonDocument.Parse(sent.Body);
        var root = body.RootElement;

        Assert.Equal("claude-opus-5-5", root.GetProperty("model").GetString());
        Assert.Equal("medium", root.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("default", root.GetProperty("fallbacks").GetString());
        Assert.Equal("server-side-fallback-2026-07-01", sent.Beta);
        Assert.Equal("the-key", sent.Key);
        Assert.Equal("2023-06-01", sent.Version);

        // This model always thinks and refuses a request that tries to switch that off or set a
        // temperature; a copy of an older request shape would be a 400 on every question.
        Assert.False(root.TryGetProperty("thinking", out _));
        Assert.False(root.TryGetProperty("temperature", out _));
        Assert.False(root.TryGetProperty("tool_choice", out _));

        // The instructions are cacheable, and the date is not in them.
        var system = root.GetProperty("system")[0];
        Assert.Equal("ephemeral", system.GetProperty("cache_control").GetProperty("type").GetString());

        Assert.Equal("list_projects", root.GetProperty("tools")[0].GetProperty("name").GetString());
    }

    /// <summary>
    /// A reply that opens with thinking and ends in a lookup. Reading the first block as the
    /// answer would show nothing; rebuilding the reply from its text would drop the thinking the
    /// provider needs back.
    /// </summary>
    [Fact]
    public async Task A_reply_is_read_by_block_type_and_its_content_kept_verbatim()
    {
        const string content = """[{"type":"thinking","thinking":"","signature":"sig"},{"type":"text","text":"Let me look."},{"type":"tool_use","id":"toolu_9","name":"list_projects","input":{}}]""";

        var network = new Network().Answer(HttpStatusCode.OK,
            $$$"""{"model":"claude-opus-5-5","stop_reason":"tool_use","content":{{{content}}},"usage":{"input_tokens":120,"output_tokens":30,"cache_read_input_tokens":400}}""");

        var reply = await Model(network).AskAsync(Question);

        Assert.Equal(ReplyEnd.WantsTools, reply.End);
        Assert.Equal("Let me look.", reply.Text);
        Assert.Equal("toolu_9", Assert.Single(reply.Calls).Id);
        Assert.Equal(520, reply.InputTokens);

        using var kept = JsonDocument.Parse(reply.Raw);
        Assert.Equal("sig", kept.RootElement[0].GetProperty("signature").GetString());

        // And it goes back as it came.
        var next = new ModelRequest(Question.Instructions,
            [.. Question.Conversation, new Replied(reply.Raw), new ToolResults([new ToolResult("toolu_9", "[]", false)])],
            Question.Tools);

        network.Answer(HttpStatusCode.OK, Finished("Nothing is late."));
        await Model(network).AskAsync(next);

        using var body = JsonDocument.Parse(network.Sent[1].Body);
        var replayed = body.RootElement.GetProperty("messages")[1].GetProperty("content");
        Assert.Equal("sig", replayed[0].GetProperty("signature").GetString());
        Assert.Equal("tool_result", body.RootElement.GetProperty("messages")[2].GetProperty("content")[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_refusal_comes_back_as_declined_rather_than_as_an_empty_answer()
    {
        var network = new Network().Answer(HttpStatusCode.OK,
            """{"model":"claude-opus-5-5","stop_reason":"refusal","content":[],"usage":{"input_tokens":5,"output_tokens":0}}""");

        var reply = await Model(network).AskAsync(Question);

        Assert.Equal(ReplyEnd.Declined, reply.End);
    }

    /// <summary>A rate limit is worth one more try; the person should not see it if the second works.</summary>
    [Fact]
    public async Task A_rate_limit_is_tried_again_and_the_second_answer_used()
    {
        var network = new Network()
            .Answer(HttpStatusCode.TooManyRequests, """{"type":"error","error":{"type":"rate_limit_error","message":"slow down"}}""", retryAfterSeconds: 0)
            .Answer(HttpStatusCode.OK, Finished("On time."));

        var reply = await Model(network).AskAsync(Question);

        Assert.Equal("On time.", reply.Text);
        Assert.Equal(2, network.Sent.Count);
    }

    [Fact]
    public async Task An_overloaded_provider_is_tried_three_times_and_then_said_in_words()
    {
        var network = new Network();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            network.Answer((HttpStatusCode)529, """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""", retryAfterSeconds: 0);
        }

        var failure = await Assert.ThrowsAsync<AiUnavailableException>(() => Model(network).AskAsync(Question));

        Assert.Equal(AiFailure.Overloaded, failure.Failure);
        Assert.Contains("overloaded", failure.Message);
        Assert.Equal(3, network.Sent.Count);
    }

    /// <summary>
    /// A refused key is not worth a second try — it will be refused again — and the sentence says
    /// what to check rather than repeating a status code.
    /// </summary>
    [Fact]
    public async Task A_refused_key_is_not_retried_and_says_what_to_check()
    {
        var network = new Network().Answer(HttpStatusCode.Unauthorized,
            """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""");

        var failure = await Assert.ThrowsAsync<AiUnavailableException>(() => Model(network).AskAsync(Question));

        Assert.Equal(AiFailure.Rejected, failure.Failure);
        Assert.Contains("Ai:ApiKey", failure.Message);
        Assert.Single(network.Sent);
    }

    [Fact]
    public async Task Silence_past_the_deadline_is_a_timeout_not_a_hung_page()
    {
        var network = new Network { Hang = true };

        var failure = await Assert.ThrowsAsync<AiUnavailableException>(
            () => Model(network, timeout: TimeSpan.FromMilliseconds(200)).AskAsync(Question));

        Assert.Equal(AiFailure.TimedOut, failure.Failure);
    }

    [Fact]
    public async Task No_key_means_no_request_at_all()
    {
        var network = new Network();

        var failure = await Assert.ThrowsAsync<AiUnavailableException>(
            () => Model(network, key: string.Empty).AskAsync(Question));

        Assert.Equal(AiFailure.NotConfigured, failure.Failure);
        Assert.Empty(network.Sent);
    }

    [Fact]
    public async Task A_reply_schema_asks_for_structured_output()
    {
        var network = new Network().Answer(HttpStatusCode.OK, Finished("{}"));

        await Model(network).AskAsync(Question with { ReplySchema = """{"type":"object"}""" });

        using var body = JsonDocument.Parse(Assert.Single(network.Sent).Body);
        var format = body.RootElement.GetProperty("output_config").GetProperty("format");

        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.Equal("object", format.GetProperty("schema").GetProperty("type").GetString());
    }

    private static string Finished(string text) =>
        $$$"""{"model":"claude-opus-5-5","stop_reason":"end_turn","content":[{"type":"text","text":{{{JsonSerializer.Serialize(text)}}}}],"usage":{"input_tokens":10,"output_tokens":3}}""";

    private static AnthropicModel Model(Network network, string key = "the-key", TimeSpan? timeout = null) =>
        new(network, Options.Create(new AiOptions
        {
            ApiKey = key,
            Timeout = timeout ?? TimeSpan.FromSeconds(30),
        }), NullLogger<AnthropicModel>.Instance);

    private sealed record Request(string Body, string? Key, string? Version, string? Beta);

    private sealed class Network : HttpMessageHandler, IHttpClientFactory
    {
        private readonly Queue<(HttpStatusCode Status, string Body, int? RetryAfter)> _answers = new();

        public List<Request> Sent { get; } = [];

        public bool Hang { get; init; }

        public Network Answer(HttpStatusCode status, string body, int? retryAfterSeconds = null)
        {
            _answers.Enqueue((status, body, retryAfterSeconds));
            return this;
        }

        public HttpClient CreateClient(string name) =>
            new(this, disposeHandler: false) { BaseAddress = new Uri("https://api.anthropic.test/") };

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent.Add(new Request(
                await request.Content!.ReadAsStringAsync(cancellationToken),
                Header(request, "x-api-key"),
                Header(request, "anthropic-version"),
                Header(request, "anthropic-beta")));

            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            var (status, body, retryAfter) = _answers.Dequeue();
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

            if (retryAfter is { } seconds)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            }

            return response;
        }

        private static string? Header(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values) ? values.Single() : null;
    }
}
