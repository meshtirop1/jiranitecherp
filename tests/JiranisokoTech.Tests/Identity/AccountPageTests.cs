using System.Net;

namespace JiranisokoTech.Tests.Identity;

/// <summary>
/// The page where somebody can see what has been done with their account.
/// </summary>
public class AccountPageTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    private const string Password = "a-long-enough-password";

    [Fact]
    public async Task A_stranger_cannot_read_it()
    {
        using var browser = factory.CreateBrowser();

        var response = await browser.GetAsync("/account");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("/sign-in", response.Headers.Location!.OriginalString);
    }

    /// <summary>
    /// The failures are the point.
    /// </summary>
    /// <remarks>
    /// Showing somebody only their successful sign-ins tells them what they
    /// already know. The reason to show a history at all is so they can spot an
    /// attempt that was not them, and an attempt that was not them is usually
    /// one that failed.
    /// </remarks>
    [Fact]
    public async Task It_shows_the_failures_as_well_as_the_successes()
    {
        await factory.CreateAccountAsync(
            "history@jiranisokotech.co.ke", Password, "Charity Jepchirchir");

        using var browser = factory.CreateBrowser();

        await SignInAsync(browser, "history@jiranisokotech.co.ke", "not-the-password");
        await SignInAsync(browser, "history@jiranisokotech.co.ke", Password);

        var page = await browser.GetAsync("/account");
        var html = await page.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Charity Jepchirchir", html);
        Assert.Contains("history@jiranisokotech.co.ke", html);
        Assert.Contains("Signed in", html);
        Assert.Contains("Refused", html);
    }

    private static async Task SignInAsync(HttpClient browser, string email, string password)
    {
        var form = await browser.GetAsync("/sign-in");

        var fields = HtmlForm.Fill(
            await form.Content.ReadAsStringAsync(),
            new Dictionary<string, string>
            {
                ["Input.Email"] = email,
                ["Input.Password"] = password,
            });

        await browser.PostAsync("/sign-in", new FormUrlEncodedContent(fields));
    }
}
