using System.Net;
using System.Net.Http.Headers;
using JiranisokoTech.Application.Api;
using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Tests.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Api;

/// <summary>
/// The API's front door: who gets in, with what, and what they may then read.
/// </summary>
/// <remarks>
/// Two authentication schemes now exist side by side, and the thing worth
/// testing is that they do not bleed into each other. A key must not open a
/// screen and a session cookie must not reach the API — otherwise a stolen
/// cookie becomes an API client, and a key committed to a repository becomes a
/// login.
/// </remarks>
public class PublicApiTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    private const string Password = "a-long-enough-password";

    [Fact]
    public async Task A_request_with_no_key_is_refused_with_a_header_not_a_login_page()
    {
        using var browser = factory.CreateBrowser();

        var response = await browser.GetAsync("/api/v1/clients");

        // 401 and a challenge header. A machine that receives a 302 to an HTML
        // form reports "the API returned a web page", which is what it did.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("Bearer nonsense")]
    [InlineData("Bearer jts_live_thisisnotarealkeyatall")]
    public async Task A_key_that_is_not_recognised_is_refused(string header)
    {
        using var browser = factory.CreateBrowser();

        browser.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", header);

        var response = await browser.GetAsync("/api/v1/clients");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_key_reads_what_its_scopes_allow()
    {
        var secret = await IssueAsync("the website", Permissions.ClientsView);

        using var browser = Calling(secret);

        var response = await browser.GetAsync("/api/v1/clients");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// And nothing else. This is the whole point of scopes.
    /// </summary>
    [Fact]
    public async Task A_key_is_refused_what_its_scopes_do_not_allow()
    {
        var secret = await IssueAsync("the website", Permissions.ClientsView);

        using var browser = Calling(secret);

        var response = await browser.GetAsync("/api/v1/invoices");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// A revoked key stops working, and says so differently from a wrong one.
    /// </summary>
    /// <remarks>
    /// Told apart on purpose. Somebody whose integration stops needs to know it
    /// was turned off rather than wonder whether they have the wrong value.
    /// </remarks>
    [Fact]
    public async Task A_revoked_key_stops_working()
    {
        var secret = await IssueAsync("a key to revoke", Permissions.ClientsView);

        using var before = Calling(secret);
        Assert.Equal(HttpStatusCode.OK, (await before.GetAsync("/api/v1/clients")).StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var keys = scope.ServiceProvider.GetRequiredService<ApiKeyService>();
            var all = await keys.AllAsync();
            var mine = all.First(key => key.Name == "a key to revoke");

            await keys.RevokeAsync(mine.Id, "The integration was retired.");
        }

        using var after = Calling(secret);

        Assert.Equal(HttpStatusCode.Unauthorized, (await after.GetAsync("/api/v1/clients")).StatusCode);
    }

    /// <summary>
    /// A session cookie does not reach the API.
    /// </summary>
    /// <remarks>
    /// The endpoints name the key scheme, so a browser session — even an
    /// owner's — is not an API client. That keeps a stolen cookie from becoming
    /// one, and it is the half of the separation that is easy to leave out,
    /// because leaving it out makes everything appear to work.
    /// </remarks>
    [Fact]
    public async Task A_signed_in_person_cannot_use_the_api_with_their_cookie()
    {
        var browser = await SignedInAsync("apiowner@jiranisokotech.co.ke", Roles.Administrator);

        var response = await browser.GetAsync("/api/v1/clients");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// And a key does not open a screen.
    /// </summary>
    [Fact]
    public async Task A_key_cannot_open_a_page()
    {
        var secret = await IssueAsync("a key for pages", Permissions.ClientsView);

        using var browser = Calling(secret);

        var response = await browser.GetAsync("/clients");

        // Sent to sign in, exactly as an anonymous visitor would be: the page
        // reads cookies and knows nothing about keys.
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("/sign-in", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Whoami_says_which_key_is_calling_and_what_it_may_do()
    {
        var secret = await IssueAsync("the job feed", Permissions.PostingsManage);

        using var browser = Calling(secret);

        var response = await browser.GetAsync("/api/v1/whoami");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("the job feed", body);
        Assert.Contains(Permissions.PostingsManage, body);
    }

    /// <summary>
    /// The secret is not in the database; a hash of it is.
    /// </summary>
    /// <remarks>
    /// So a copy of the table is not a set of working keys. Asserted here
    /// rather than trusted, because the failure mode is invisible: everything
    /// works either way.
    /// </remarks>
    [Fact]
    public async Task The_secret_is_never_stored()
    {
        var secret = await IssueAsync("a key to inspect", Permissions.ClientsView);

        using var scope = factory.Services.CreateScope();
        var keys = scope.ServiceProvider.GetRequiredService<ApiKeyService>();

        var stored = (await keys.AllAsync()).First(key => key.Name == "a key to inspect");

        Assert.NotEqual(secret, stored.Hash);
        Assert.DoesNotContain(secret, stored.Hash);
        Assert.EndsWith(stored.Hint, secret);
    }

    [Fact]
    public async Task A_scope_that_is_not_a_permission_is_refused()
    {
        using var scope = factory.Services.CreateScope();
        var keys = scope.ServiceProvider.GetRequiredService<ApiKeyService>();

        await Assert.ThrowsAsync<ArgumentException>(
            () => keys.IssueAsync("a bad key", ["clients.everything"], null));
    }

    private async Task<string> IssueAsync(string name, params string[] scopes)
    {
        using var scope = factory.Services.CreateScope();
        var keys = scope.ServiceProvider.GetRequiredService<ApiKeyService>();

        var issued = await keys.IssueAsync(name, scopes, null);

        return issued.Secret;
    }

    private HttpClient Calling(string secret)
    {
        var browser = factory.CreateBrowser();

        browser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", secret);

        return browser;
    }

    private async Task<HttpClient> SignedInAsync(string email, string role)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            if (await users.FindByEmailAsync(email) is null)
            {
                await factory.CreateAccountAsync(email, Password, email);
            }

            var stored = await users.FindByEmailAsync(email);

            if (!await users.IsInRoleAsync(stored!, role))
            {
                await users.AddToRoleAsync(stored!, role);
            }
        }

        var browser = factory.CreateBrowser();

        var form = await browser.GetAsync("/sign-in");
        var fields = HtmlForm.Fill(
            await form.Content.ReadAsStringAsync(),
            new Dictionary<string, string>
            {
                ["Input.Email"] = email,
                ["Input.Password"] = Password,
            });

        await browser.PostAsync("/sign-in", new FormUrlEncodedContent(fields));

        return browser;
    }
}
