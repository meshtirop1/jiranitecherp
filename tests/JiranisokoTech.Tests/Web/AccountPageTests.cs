using System.Net;
using System.Text.RegularExpressions;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Tests.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// Opening an account through the application, end to end.
/// </summary>
/// <remarks>
/// Until this existed there was no way to create a second account at all: the
/// bootstrap owner was the only person who could sign in, and every other
/// account in this suite was made in code. So the test that matters is the
/// whole round trip — an administrator opens one, and the person it was opened
/// for gets in on a password nobody else ever knew.
/// </remarks>
public partial class AccountPageTests(ApplicationFactory factory)
    : IClassFixture<ApplicationFactory>
{
    private const string Password = "a-long-enough-password";

    [Fact]
    public async Task A_stranger_is_sent_to_sign_in()
    {
        using var browser = factory.CreateBrowser();

        var response = await browser.GetAsync("/accounts");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("/sign-in", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task An_engineer_cannot_open_the_accounts_page()
    {
        var developer = await SignedInAsync("dev-accounts@jiranisokotech.co.ke", Roles.Developer);

        var response = await developer.GetAsync("/accounts");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("/denied", response.Headers.Location!.OriginalString);
    }

    /// <summary>
    /// The whole point of the module, in one test.
    /// </summary>
    /// <remarks>
    /// An administrator opens an account and never sees a password. The person
    /// follows the link, chooses one, and signs in. Nobody else ever knew it,
    /// which is the property that makes the audit trail mean anything.
    /// </remarks>
    [Fact]
    public async Task An_administrator_opens_an_account_and_the_person_lets_themselves_in()
    {
        var administrator = await SignedInAsync(
            "opener@jiranisokotech.co.ke", Roles.Administrator);

        var page = await administrator.GetAsync("/accounts");
        var fields = HtmlForm.Fill(
            await page.Content.ReadAsStringAsync(),
            new Dictionary<string, string>
            {
                ["Input.DisplayName"] = "Precious",
                ["Input.Email"] = "precious@jiranisokotech.co.ke",
                ["Input.JobTitle"] = "Secretary and Accounts Officer",
            });

        var opened = await administrator.PostAsync("/accounts", new FormUrlEncodedContent(fields));
        var html = await opened.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        Assert.Contains("Precious", html);

        // The link is on the page because nothing emails it yet, which the page
        // says rather than hides.
        Assert.Contains("Nothing emails it for them", html);

        var link = LinkIn(html);
        Assert.NotNull(link);

        // Until they set a password, nothing gets them in.
        Assert.Equal(
            SignInOutcome.Refused,
            await AttemptAsync("precious@jiranisokotech.co.ke", Password));

        using var invitee = factory.CreateBrowser();

        var form = await invitee.GetAsync(link!);
        var setting = HtmlForm.Fill(
            await form.Content.ReadAsStringAsync(),
            new Dictionary<string, string>
            {
                ["Input.Password"] = Password,
                ["Input.Again"] = Password,
            });

        var set = await invitee.PostAsync(link!, new FormUrlEncodedContent(setting));

        Assert.Contains("That is set", await set.Content.ReadAsStringAsync());

        Assert.Equal(
            SignInOutcome.Succeeded,
            await AttemptAsync("precious@jiranisokotech.co.ke", Password));
    }

    [Fact]
    public async Task A_link_that_is_incomplete_says_so_rather_than_failing_oddly()
    {
        using var browser = factory.CreateBrowser();

        var form = await browser.GetAsync("/set-password");
        var fields = HtmlForm.Fill(
            await form.Content.ReadAsStringAsync(),
            new Dictionary<string, string>
            {
                ["Input.Password"] = Password,
                ["Input.Again"] = Password,
            });

        var posted = await browser.PostAsync("/set-password", new FormUrlEncodedContent(fields));

        Assert.Contains("link is incomplete", await posted.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The page anybody can reach without signing in, so it is worth knowing it
    /// is reachable at all — a deny-by-default policy would otherwise send the
    /// invited person to a login they cannot pass.
    /// </summary>
    [Fact]
    public async Task The_set_password_page_is_reachable_without_signing_in()
    {
        using var browser = factory.CreateBrowser();

        var response = await browser.GetAsync("/set-password?email=a@b.co&token=x");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Set your password", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_accounts_list_says_who_has_no_staff_record()
    {
        var administrator = await SignedInAsync(
            "lister@jiranisokotech.co.ke", Roles.Administrator);

        var html = await (await administrator.GetAsync("/accounts")).Content.ReadAsStringAsync();

        // Every account in this suite is opened without one, so the column has
        // to be saying it.
        Assert.Contains("Not linked", html);
    }

    [GeneratedRegex("""<code class="link">([^<]+)</code>""")]
    private static partial Regex SignInLink();

    private static string? LinkIn(string html)
    {
        var match = SignInLink().Match(html);

        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Value) : null;
    }

    private Task<SignInOutcome> AttemptAsync(string email, string password) =>
        factory.InRequestAsync(services =>
            services.GetRequiredService<JiranisokoTech.Web.Identity.SignInService>()
                .PasswordSignInAsync(
                    email, password, remember: false, ipAddress: null, userAgent: null));

    private async Task<HttpClient> SignedInAsync(string email, string role)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            if (await users.FindByEmailAsync(email) is null)
            {
                await factory.CreateAccountAsync(email, Password, email);

                var stored = await users.FindByEmailAsync(email);
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
