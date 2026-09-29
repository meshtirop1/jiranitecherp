using System.Text.RegularExpressions;

namespace JiranisokoTech.Tests.Infrastructure;

/// <summary>
/// The decisions in the deployment files that would otherwise drift, held where a test sees them.
/// </summary>
/// <remarks>
/// Section 87. The compose file and the Caddyfile cannot be exercised by the suite — there is no
/// Docker daemon in a test run — so what is checked is the text, and only the parts that went
/// wrong or would go wrong silently. They were validated by hand as well: <c>docker compose
/// config</c> for the compose file, <c>caddy validate</c> for the Caddyfile, and Caddy run in
/// front of the published application to see a page served through it.
/// </remarks>
public class DeploymentTests
{
    private static readonly string Root = FindRoot();

    private static readonly string Compose =
        File.ReadAllText(Path.Combine(Root, "docker", "compose.yaml"));

    /// <summary>
    /// The bundled proxy starts only when asked for.
    /// </summary>
    /// <remarks>
    /// A host that already runs nginx in front of its containers has 80 and 443 taken. A proxy
    /// that started by default would fail to bind them and take the whole <c>up</c> with it.
    /// </remarks>
    [RepositoryFact]
    public void The_proxy_is_behind_a_profile()
    {
        var proxy = Service("proxy");

        Assert.Contains("profiles: [\"proxy\"]", proxy);
        Assert.Contains("./Caddyfile:/etc/caddy/Caddyfile:ro", proxy);
    }

    /// <summary>
    /// The proxy is never given an empty email.
    /// </summary>
    /// <remarks>
    /// Found by running it: Caddy refuses to start on an <c>email</c> option with nothing after
    /// it, and compose passes an unset variable as an empty string. The comment beside it said
    /// blank was fine.
    /// </remarks>
    [RepositoryFact]
    public void The_proxy_is_never_given_an_empty_email() =>
        Assert.Matches(@"ACME_EMAIL: \$\{ACME_EMAIL:-[^}\s]+@[^}\s]+\}", Service("proxy"));

    /// <summary>
    /// The proxy sets no security header of its own.
    /// </summary>
    /// <remarks>
    /// The application sets all of them. Two places setting the same header is how they come to
    /// disagree, and a browser given two content security policies enforces both.
    /// </remarks>
    [RepositoryFact]
    public void The_proxy_adds_no_security_headers()
    {
        var caddy = File.ReadAllText(Path.Combine(Root, "docker", "Caddyfile"));

        foreach (var header in new[]
                 {
                     "Strict-Transport-Security", "Content-Security-Policy",
                     "X-Frame-Options", "X-Content-Type-Options", "Referrer-Policy",
                 })
        {
            Assert.DoesNotContain(header, caddy, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The certificates survive the proxy being recreated.</summary>
    [RepositoryFact]
    public void The_certificates_are_on_a_volume() =>
        Assert.Contains("caddy-data:/data", Service("proxy"));

    /// <summary>
    /// The application and the database are not published to the internet.
    /// </summary>
    [RepositoryFact]
    public void Only_the_proxy_listens_beyond_this_host()
    {
        Assert.Contains("\"127.0.0.1:${WEB_PORT:-8080}:8080\"", Service("web"));
        Assert.DoesNotContain("ports:", Service("db"));
        Assert.DoesNotContain("ports:", Service("cache"));
    }

    /// <summary>The block of one service, up to the next service at the same indentation.</summary>
    private static string Service(string name)
    {
        var match = Regex.Match(
            Compose,
            $@"^  {name}:\n(?<body>(?:(?:    .*|\s*)\n)+?)(?=^  \S|^\S)",
            RegexOptions.Multiline);

        Assert.True(match.Success, $"No service called {name} in compose.yaml.");

        return match.Groups["body"].Value;
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ".env.example")))
        {
            directory = directory.Parent;
        }

        return directory!.FullName;
    }
}
