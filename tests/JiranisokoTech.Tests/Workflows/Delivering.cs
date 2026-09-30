using System.Net;
using System.Security.Cryptography;
using System.Text;
using JiranisokoTech.Application.Engineering;
using JiranisokoTech.Infrastructure.Messaging;
using JiranisokoTech.Tests.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace JiranisokoTech.Tests.Workflows;

/// <summary>
/// Putting a signed delivery through the webhook endpoint, and draining what it raises.
/// </summary>
/// <remarks>
/// Shared because two walks need it and a copy of a helper is a copy of its bug — the rule
/// CLAUDE.md states after six pages each grew their own <c>Length(TimeSpan)</c> and one of them
/// started writing "1 days". This one would be worse than that: the signature is the part a
/// delivery is refused for, and two versions of it drifting would produce a walk that fails with
/// a 401 the reader has to go and decode.
///
/// The endpoint is the real one. Nothing here reaches into the dispatcher to fake an arrival,
/// because the fault this proves against — a refusal answered with a bare status code, which
/// <c>UseStatusCodePagesWithReExecute</c> replays as a 400 — lives in the middleware between the
/// two.
/// </remarks>
public static class Delivering
{
    /// <summary>
    /// Post one delivery as the provider would, signed, and insist it was accepted.
    /// </summary>
    /// <remarks>
    /// The assertion on OK is not ceremony. A signature mismatch answers 401 and an unknown
    /// repository answers 200-and-ignores, so a walk that did not check would carry on and fail
    /// several steps later on an empty table — which is the shape of failure that sends somebody
    /// to read the wrong code.
    /// </remarks>
    public static async Task SignedAsync(
        ApplicationFactory factory, string kind, string payload, string id)
    {
        using var browser = factory.CreateBrowser();

        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/github")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        request.Headers.TryAddWithoutValidation("X-GitHub-Event", kind);
        request.Headers.TryAddWithoutValidation("X-GitHub-Delivery", id);
        request.Headers.TryAddWithoutValidation(
            "X-Hub-Signature-256",
            "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(Workflow.GitHubSecret),
                Encoding.UTF8.GetBytes(payload))));

        var response = await browser.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Process what arrived, then what processing it raised — the two loops production runs.
    /// </summary>
    /// <remarks>
    /// In that order, and both of them. A delivery is written to the inbox by the endpoint and
    /// turned into records by the first loop; the records raise domain events that the second
    /// loop dispatches. Draining only the first leaves a walk asserting on a board that has not
    /// been told anything yet.
    ///
    /// <c>RunOnceAsync</c> takes ONE batch, which its name says and the background processor
    /// depends on. Every caller here delivers a handful of messages, so one pass settles them;
    /// anything writing in bulk has to loop until it settles nothing, bounded — the demonstration
    /// seed settled fifty of two hundred and twenty-nine believing otherwise.
    /// </remarks>
    public static async Task SettleAsync(ApplicationFactory factory)
    {
        await factory.InScopeAsync(services =>
            services.GetRequiredService<DeliveryDispatcher>().RunOnceAsync());

        await factory.InScopeAsync(services =>
            services.GetRequiredService<OutboxDispatcher>().RunOnceAsync());
    }
}
