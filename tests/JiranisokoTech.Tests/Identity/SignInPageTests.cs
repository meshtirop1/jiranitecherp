using System.Net;

namespace JiranisokoTech.Tests.Identity;

/// <summary>
/// The sign-in form, over HTTP, through the whole pipeline.
/// </summary>
/// <remarks>
/// What breaks sign-in in practice is almost never the password check. It is a
/// cookie that is never written because the page was rendered interactively, a
/// missing antiforgery token, or an authorization policy that refuses the very
/// page somebody has to reach in order to authenticate. None of those are
/// visible below the HTTP layer, which is why these go through it.
/// </remarks>
public class SignInPageTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    private const string Password = "a-long-enough-password";
    private const string SessionCookie = ".AspNetCore.Identity.Application";

    /// <summary>
    /// The page a deny-by-default policy is likeliest to lock, because it is
    /// the one nobody can be authenticated for yet.
    /// </summary>
    [Fact]
    public async Task The_form_is_reachable_without_signing_in()
    {
        using var browser = factory.CreateBrowser();

        var response = await browser.GetAsync("/sign-in");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Sign in", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Signing_in_sets_a_session_cookie_and_lands_on_the_home_page()
    {
        await factory.CreateAccountAsync("page@jiranisokotech.co.ke", Password, "Vincent Bungei");

        using var browser = factory.CreateBrowser();

        var response = await PostAsync(browser, "/sign-in", "page@jiranisokotech.co.ke", Password);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", Landing(response).AbsolutePath);
        Assert.Contains(SessionCookie, string.Join(";", Cookies(response)));

        // And the session is accepted on the next request. A cookie that is set
        // but never honoured looks identical from here without this.
        var home = await browser.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Contains("Vincent Bungei", await home.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_wrong_password_returns_the_form_and_no_cookie()
    {
        await factory.CreateAccountAsync("refused@jiranisokotech.co.ke", Password);

        using var browser = factory.CreateBrowser();

        var response = await PostAsync(
            browser, "/sign-in", "refused@jiranisokotech.co.ke", "not-the-password");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(SessionCookie, string.Join(";", Cookies(response)));
        Assert.Contains("do not match an account", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The one that matters most here.
    /// </summary>
    /// <remarks>
    /// If a wrong password and an unknown address read differently, this form
    /// is a way of finding out who works at this company: ask it a thousand
    /// addresses and it answers every one. The two replies have to be the same
    /// words, and a test is the only thing that keeps them so.
    /// </remarks>
    [Fact]
    public async Task An_unknown_address_is_answered_exactly_as_a_wrong_password_is()
    {
        await factory.CreateAccountAsync("known@jiranisokotech.co.ke", Password);

        using var browser = factory.CreateBrowser();

        var wrongPassword = await PostAsync(
            browser, "/sign-in", "known@jiranisokotech.co.ke", "not-the-password");

        var noSuchAccount = await PostAsync(
            browser, "/sign-in", "stranger@example.com", "not-the-password");

        Assert.Equal(wrongPassword.StatusCode, noSuchAccount.StatusCode);
        Assert.Equal(
            Refusal(await wrongPassword.Content.ReadAsStringAsync()),
            Refusal(await noSuchAccount.Content.ReadAsStringAsync()));
    }

    /// <summary>A post carrying no antiforgery token is not somebody using the form.</summary>
    [Fact]
    public async Task A_post_without_a_token_is_refused()
    {
        await factory.CreateAccountAsync("forged@jiranisokotech.co.ke", Password);

        using var browser = factory.CreateBrowser();

        var response = await browser.PostAsync("/sign-in", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["_handler"] = "sign-in",
                ["Input.Email"] = "forged@jiranisokotech.co.ke",
                ["Input.Password"] = Password,
            }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(SessionCookie, string.Join(";", Cookies(response)));
    }

    /// <summary>
    /// An unchecked return address is an open redirect: our own sign-in link
    /// landing somebody on a site that is not ours, with our domain in the
    /// address bar the whole way there.
    /// </summary>
    [Fact]
    public async Task A_return_address_pointing_off_site_is_ignored()
    {
        await factory.CreateAccountAsync("return@jiranisokotech.co.ke", Password);

        using var browser = factory.CreateBrowser();

        var response = await PostAsync(
            browser,
            "/sign-in?returnUrl=https://evil.example/collect",
            "return@jiranisokotech.co.ke",
            Password);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        // The host is the assertion that matters: a redirect to the root of
        // somebody else's domain would satisfy a path check.
        Assert.Equal("localhost", Landing(response).Host);
        Assert.Equal("/", Landing(response).AbsolutePath);
    }

    [Fact]
    public async Task A_return_address_inside_the_application_is_honoured()
    {
        await factory.CreateAccountAsync("deep@jiranisokotech.co.ke", Password);

        using var browser = factory.CreateBrowser();

        var response = await PostAsync(
            browser, "/sign-in?returnUrl=/denied", "deep@jiranisokotech.co.ke", Password);

        Assert.Equal("/denied", Landing(response).AbsolutePath);
    }

    [Fact]
    public async Task Signing_out_ends_the_session()
    {
        await factory.CreateAccountAsync("out@jiranisokotech.co.ke", Password);

        using var browser = factory.CreateBrowser();

        await PostAsync(browser, "/sign-in", "out@jiranisokotech.co.ke", Password);

        // Read from a page the signed-in person is actually looking at: the
        // sign-out control lives in the navigation, not on a page of its own.
        var home = await browser.GetAsync("/");
        var fields = HtmlForm.Fill(await home.Content.ReadAsStringAsync());

        var signedOut = await browser.PostAsync("/sign-out", new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);

        var after = await browser.GetAsync("/");

        Assert.Equal(HttpStatusCode.Found, after.StatusCode);
        Assert.Contains("/sign-in", after.Headers.Location!.OriginalString);
    }

    /// <summary>
    /// Signing out on a GET is a URL, and a URL is something another site can
    /// put in an image tag to sign a colleague out from across the internet.
    /// </summary>
    [Fact]
    public async Task Following_a_link_cannot_sign_somebody_out()
    {
        await factory.CreateAccountAsync("linked@jiranisokotech.co.ke", Password);

        using var browser = factory.CreateBrowser();

        await PostAsync(browser, "/sign-in", "linked@jiranisokotech.co.ke", Password);

        await browser.GetAsync("/sign-out");

        // Whatever that GET answered, the session survived it.
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/")).StatusCode);
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient browser, string url, string email, string password)
    {
        var form = await browser.GetAsync(url);

        var fields = HtmlForm.Fill(
            await form.Content.ReadAsStringAsync(),
            new Dictionary<string, string>
            {
                ["Input.Email"] = email,
                ["Input.Password"] = password,
            });

        return await browser.PostAsync(url, new FormUrlEncodedContent(fields));
    }

    /// <summary>
    /// Where a response sends the browser next, always as an absolute address.
    /// </summary>
    /// <remarks>
    /// A redirect header may be written either way, and the framework resolves
    /// ours against the request. Comparing whole strings would make these tests
    /// fail on a formatting change and, worse, pass a redirect to another host
    /// whose path happened to match.
    /// </remarks>
    private static Uri Landing(HttpResponseMessage response) =>
        new(new Uri("https://localhost"), response.Headers.Location!);

    private static IEnumerable<string> Cookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [];

    /// <summary>The refusal alone, so the comparison is of words and not markup.</summary>
    private static string Refusal(string html)
    {
        const string marker = "role=\"alert\"";

        var at = html.IndexOf(marker, StringComparison.Ordinal);

        Assert.True(at >= 0, "The page showed no refusal at all.");

        var opens = html.IndexOf('>', at) + 1;
        var closes = html.IndexOf("</p>", opens, StringComparison.Ordinal);

        return html[opens..closes].Trim();
    }
}
