using System.Collections.Concurrent;
using JiranisokoTech.Application.Ai;
using JiranisokoTech.Tests.Identity;
using JiranisokoTech.Tests.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JiranisokoTech.Tests.Ai;

/// <summary>
/// A model that says what the test tells it to, and remembers everything it was sent.
/// </summary>
/// <remarks>
/// No test reaches the real provider: it costs money, needs a key the suite does not have, and
/// would make a green run depend on somebody else's network. What these tests need from a model is
/// not intelligence but two things a real one cannot give — a scripted reply, so the lookup it asks
/// for is known in advance, and a record of every request, so a test can assert on exactly what
/// left the building. The second is what the permission tests are built on: a refused lookup is
/// proved not by what the page says but by the records never appearing in anything sent.
/// </remarks>
public sealed class FakeModel : IAiModel
{
    private readonly ConcurrentQueue<Func<ModelRequest, ModelReply>> _script = new();

    public bool IsConfigured { get; set; } = true;

    public string Name => "fake-model";

    public ConcurrentQueue<ModelRequest> Received { get; } = new();

    /// <summary>What every request sent, flattened, for asserting a string never left.</summary>
    public string Everything => string.Join("\n", Received.SelectMany(request =>
        request.Conversation.Select(turn => turn switch
        {
            Asked asked => asked.Text + string.Concat((asked.Enclosures ?? []).Select(one => one.Title)),
            Replied replied => replied.Raw,
            ToolResults results => string.Join("\n", results.Results.Select(result => result.Content)),
            _ => string.Empty,
        })));

    /// <summary>Forget the script and the history, between tests sharing one factory.</summary>
    public void Reset()
    {
        IsConfigured = true;
        _script.Clear();
        Received.Clear();
    }

    public FakeModel Then(Func<ModelRequest, ModelReply> reply)
    {
        _script.Enqueue(reply);
        return this;
    }

    /// <summary>Reply by asking for one lookup.</summary>
    public FakeModel ThenLooksUp(string tool, string input) =>
        Then(_ => new ModelReply(ReplyEnd.WantsTools, string.Empty,
            [new ToolCall($"call-{Guid.NewGuid():N}", tool, input)], """[{"type":"tool_use"}]""", 10, 5, Name));

    /// <summary>Reply with a finished answer.</summary>
    public FakeModel ThenSays(string text) =>
        Then(_ => new ModelReply(ReplyEnd.Finished, text, [], """[{"type":"text"}]""", 10, 5, Name));

    public FakeModel ThenFails(AiFailure failure, string message) =>
        Then(_ => throw new AiUnavailableException(failure, message));

    public Task<ModelReply> AskAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        Received.Enqueue(request);

        if (!_script.TryDequeue(out var next))
        {
            throw new InvalidOperationException("The fake model was asked more often than the test scripted.");
        }

        return Task.FromResult(next(request));
    }
}

/// <summary>The real application with the fake model in place of the provider.</summary>
public class AiFactory : ApplicationFactory
{
    public FakeModel Model { get; } = new();

    public string Cvs { get; } = Path.Combine(Path.GetTempPath(), $"cvs-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        AiFactories.Configure(builder, Model, Cvs);
    }
}

public sealed class PostgresAiFactory : PostgresApplicationFactory
{
    public FakeModel Model { get; } = new();

    public string Cvs { get; } = Path.Combine(Path.GetTempPath(), $"cvs-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        AiFactories.Configure(builder, Model, Cvs);
    }
}

public static class AiFactories
{
    public static void Configure(IWebHostBuilder builder, FakeModel model, string cvs)
    {
        // Letters are not what these tests check, and the file transport would write them into the
        // test run's working directory; CVs go to a directory of this factory's own for the same reason.
        builder.UseSetting("Mail:Transport", "None");
        builder.UseSetting("Cvs:Directory", cvs);
        builder.UseSetting("Ai:DailyLimit", "25");

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAiModel>();
            services.AddSingleton<IAiModel>(model);
        });
    }
}
