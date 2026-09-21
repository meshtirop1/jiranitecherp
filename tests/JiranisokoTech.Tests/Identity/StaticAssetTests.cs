using System.Net;

namespace JiranisokoTech.Tests.Identity;

/// <summary>
/// The stylesheets and scripts, which belong to no account.
/// </summary>
/// <remarks>
/// This exists because of a fault that shipped and was only found by looking at
/// the page. The deny-by-default authorization policy applies to every endpoint
/// without an opinion of its own, and static assets are endpoints — so every
/// css and js request was answered with a redirect to the sign-in page. The
/// browser asked for a stylesheet, received HTML, discarded it, and rendered
/// the login form with no styling whatsoever.
///
/// Nothing that reads markup can see that: the HTML was correct throughout, and
/// every test on it passed. What is asserted here is the content type, because
/// that is the thing that was wrong.
/// </remarks>
public class StaticAssetTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    [Theory]
    [InlineData("/app.css")]
    [InlineData("/lib/bootstrap/dist/css/bootstrap.min.css")]
    public async Task A_stylesheet_is_served_as_a_stylesheet_to_a_stranger(string path)
    {
        using var browser = factory.CreateBrowser();

        var response = await browser.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/css", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task The_framework_script_is_served_as_a_script_to_a_stranger()
    {
        using var browser = factory.CreateBrowser();

        var response = await browser.GetAsync("/_framework/blazor.web.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("javascript", response.Content.Headers.ContentType?.MediaType ?? string.Empty);
    }
}
