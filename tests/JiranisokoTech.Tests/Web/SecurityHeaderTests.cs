using System.Net;
using JiranisokoTech.Tests.Identity;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// The headers that tell a browser what it may do with this application.
/// </summary>
/// <remarks>
/// Each of these is a defence the browser applies on our behalf, and each is
/// absent unless sent. That makes them the easiest protection to lose: nothing
/// breaks when they go, no test fails, and the application looks exactly the
/// same. This build shipped with only HSTS for weeks while the system it
/// replaces set four of them.
/// </remarks>
public class SecurityHeaderTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    /// <summary>
    /// On a page, on a static asset, and on the API.
    /// </summary>
    /// <remarks>
    /// All three, because the response that matters is whichever one an
    /// attacker can get a browser to treat as a document, and that is rarely
    /// the one anybody expected. A stylesheet served without nosniff is a
    /// stylesheet a browser may decide is something else.
    /// </remarks>
    [Theory]
    [InlineData("/sign-in")]
    [InlineData("/app.css")]
    [InlineData("/api/v1/clients")]
    public async Task Every_response_carries_the_headers(string path)
    {
        using var browser = factory.CreateBrowser();

        var response = await browser.GetAsync(path);

        Assert.Equal("nosniff", Value(response, "X-Content-Type-Options"));
        Assert.Equal("DENY", Value(response, "X-Frame-Options"));
        Assert.Equal("strict-origin-when-cross-origin", Value(response, "Referrer-Policy"));
        Assert.Contains("frame-ancestors 'none'", Value(response, "Content-Security-Policy"));
    }

    /// <summary>
    /// The policy refuses the four things it can refuse here.
    /// </summary>
    /// <remarks>
    /// Asserted individually rather than as one string, so that adding a
    /// directive does not fail this and removing one does.
    /// </remarks>
    [Theory]
    [InlineData("frame-ancestors 'none'")]
    [InlineData("base-uri 'self'")]
    [InlineData("form-action 'self'")]
    [InlineData("object-src 'none'")]
    [InlineData("connect-src 'self'")]
    public async Task The_policy_carries_each_directive(string directive)
    {
        using var browser = factory.CreateBrowser();

        var response = await browser.GetAsync("/sign-in");

        Assert.Contains(directive, Value(response, "Content-Security-Policy"));
    }

    /// <summary>
    /// The policy does not claim to restrict scripts while permitting all of
    /// them.
    /// </summary>
    /// <remarks>
    /// script-src is deliberately absent: Blazor renders its import map inline
    /// and the framework needs it, so a script-src without a nonce on that tag
    /// breaks the application. The tempting fix is 'unsafe-inline', which
    /// allows every injected script on the page while reading, to anybody
    /// scanning the headers, as though scripts were restricted. This test
    /// exists so that somebody reaching for it has to delete a test that says
    /// why not.
    /// </remarks>
    [Fact]
    public async Task The_policy_does_not_pretend_to_restrict_scripts()
    {
        using var browser = factory.CreateBrowser();

        var response = await browser.GetAsync("/sign-in");
        var policy = Value(response, "Content-Security-Policy");

        Assert.DoesNotContain("unsafe-inline", policy);
        Assert.DoesNotContain("unsafe-eval", policy);

        /*
         * And no default-src either, which is the same mistake wearing a
         * different name. default-src is the fallback for every fetch
         * directive not named, script-src included — so setting it while
         * leaving script-src out does restrict scripts, silently, and blocks
         * Blazor's inline import map. The first version of this policy did
         * exactly that and nothing visibly broke, because nothing here is
         * interactive yet.
         */
        Assert.DoesNotContain("default-src", policy);
    }

    /// <summary>
    /// No page carries an inline event handler.
    /// </summary>
    /// <remarks>
    /// The drawer had an onclick attribute from the project template. It is the
    /// one thing standing between this policy and a real script-src, so it is
    /// worth a test rather than a note: an inline handler added back would be
    /// invisible until somebody tried to tighten the policy and found the
    /// application broken.
    /// </remarks>
    [Fact]
    public void No_component_uses_an_inline_event_handler()
    {
        var web = Path.Combine(SolutionRoot(), "src", "JiranisokoTech.Web");

        var offenders = Directory
            .EnumerateFiles(web, "*.razor", SearchOption.AllDirectories)
            .Where(path => System.Text.RegularExpressions.Regex.IsMatch(
                File.ReadAllText(path), @"\son(click|change|submit|load|error)\s*="))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "These components use an inline event handler, which a script-src policy refuses: "
            + string.Join(", ", offenders));
    }

    private static string Value(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
            ? string.Join(' ', values)
            : string.Empty;

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "JiranisokoTech.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }
}
