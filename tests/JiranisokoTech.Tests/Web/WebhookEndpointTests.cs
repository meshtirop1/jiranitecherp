using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using JiranisokoTech.Tests.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// The status code the Git host actually receives.
/// </summary>
/// <remarks>
/// This file exists because of a fault that no unit test could have found and
/// that running the application did find.
///
/// UseStatusCodePagesWithReExecute replays any failing response that has no body
/// of its own against /not-found, keeping the request's method. On a POST that
/// replay reaches a Blazor endpoint which requires an antiforgery token the Git
/// host has never heard of, so the replay is refused — and every empty-bodied
/// refusal from this endpoint arrived at the provider as 400, whatever it had
/// been.
///
/// For the 401 that is merely misleading. For the 503 that says no secret is
/// configured it is much worse: GitHub retries a 5xx and gives up permanently on
/// a 4xx, so a misconfiguration somebody could have fixed in ten minutes was
/// instead discarding every delivery that arrived during it, for good.
///
/// So these tests assert the number, not the behaviour behind it. The number is
/// the whole of what the provider acts on.
/// </remarks>
public class WebhookEndpointTests
{
    private const string Secret = "the-secret-github-also-holds";

    private const string Body =
        """{"ref":"refs/heads/main","repository":{"full_name":"nobody/nothing"},"commits":[]}""";

    [Fact]
    public async Task An_unsigned_delivery_is_refused_as_unauthorised()
    {
        using var factory = Configured(Secret);

        var response = await PostAsync(factory, signature: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_delivery_signed_with_the_wrong_secret_is_refused_as_unauthorised()
    {
        using var factory = Configured(Secret);

        var response = await PostAsync(factory, Signed(Body, "somebody-elses-secret"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A correctly signed delivery is accepted, even for a repository nobody
    /// connected.
    /// </summary>
    /// <remarks>
    /// Accepted rather than refused, because the provider is asking whether the
    /// delivery arrived and it did. It is recorded and ignored, which is the
    /// evidence that somebody pointed a webhook here and nothing is listening.
    /// </remarks>
    [Fact]
    public async Task A_signed_delivery_is_accepted()
    {
        using var factory = Configured(Secret);

        var response = await PostAsync(factory, Signed(Body, Secret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>A signed delivery with nothing to identify it is a bad request.</summary>
    [Fact]
    public async Task A_delivery_with_no_identifier_is_a_bad_request()
    {
        using var factory = Configured(Secret);

        var response = await PostAsync(factory, Signed(Body, Secret), deliveryId: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// With no secret configured, the answer is 503 and not 400.
    /// </summary>
    /// <remarks>
    /// The test this file was written for. 503 tells the provider to try again,
    /// so the deliveries that arrive while somebody is fixing the configuration
    /// are still delivered afterwards. 400 tells it to give up, and those
    /// deliveries are gone.
    /// </remarks>
    [Fact]
    public async Task With_no_secret_configured_the_provider_is_told_to_try_again()
    {
        using var factory = Configured(secret: null);

        var response = await PostAsync(factory, Signed(Body, Secret));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    /// <summary>
    /// Every refusal carries a body, which is what keeps it out of the status
    /// code pages middleware.
    /// </summary>
    /// <remarks>
    /// Asserted directly, because it is the mechanism the correct status codes
    /// above depend on. A future change that returned a bare status from here
    /// would put all of them back to 400, and this is the test that would say so
    /// rather than leaving it to be noticed in a provider's delivery log.
    /// </remarks>
    [Fact]
    public async Task A_refusal_explains_itself_rather_than_answering_with_an_empty_body()
    {
        using var factory = Configured(Secret);

        var response = await PostAsync(factory, signature: null);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("signature", body);
    }

    /// <summary>
    /// A page still gets the friendly error, so the fix did not cost anything.
    /// </summary>
    [Fact]
    public async Task A_missing_page_still_goes_through_the_not_found_page()
    {
        using var factory = Configured(Secret);
        using var browser = factory.CreateBrowser();

        var response = await browser.GetAsync("/no-such-page-exists");

        // Sent to sign in, because an unknown address is still behind
        // authentication — and importantly not a 500.
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostAsync(
        ApplicationFactory factory,
        string? signature,
        string? deliveryId = "d-1")
    {
        using var browser = factory.CreateBrowser();

        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/github")
        {
            Content = new StringContent(Body, Encoding.UTF8, "application/json"),
        };

        request.Headers.TryAddWithoutValidation("X-GitHub-Event", "push");

        if (deliveryId is not null)
        {
            request.Headers.TryAddWithoutValidation("X-GitHub-Delivery", deliveryId);
        }

        if (signature is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Hub-Signature-256", signature);
        }

        return await browser.SendAsync(request);
    }

    /// <summary>
    /// A host with, or without, a webhook secret.
    /// </summary>
    /// <remarks>
    /// Its own factory per test rather than a shared one, because the secret is
    /// read from configuration at startup and the whole point of one of these
    /// tests is that it is absent.
    /// </remarks>
    private static ApplicationFactory Configured(string? secret) =>
        secret is null
            ? new ApplicationFactory()
            : new SecretConfigured(secret);

    private sealed class SecretConfigured(string secret) : ApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.UseSetting("Git:Providers:GitHub:Secret", secret);
        }
    }

    private static string Signed(string body, string secret) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));
}
