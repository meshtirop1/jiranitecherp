using System.Text.Json;
using System.Text.RegularExpressions;
using JiranisokoTech.Web;

namespace JiranisokoTech.Tests.Infrastructure;

/// <summary>
/// The settings a host can be given are listed where somebody setting up a host will look.
/// </summary>
/// <remarks>
/// <b>These exist because the compose file used a variable nobody could find.</b>
/// <c>CACHE_CONNECTION</c> was read by <c>docker/compose.yaml</c> and appeared neither in
/// <c>.env.example</c> nor anywhere else, and the secrets for three of the four code hosts the
/// application understands were not passed through at all — a GitLab repository could be
/// connected and would then refuse every delivery. Section 86 asks for every environment
/// variable to be documented; these hold the compose file, the example and
/// <c>docs/configuration.md</c> to one list.
/// </remarks>
public partial class ConfigurationTests
{
    [RepositoryFact]
    public void Every_variable_the_compose_file_reads_is_in_the_example_and_the_docs()
    {
        var root = Root();
        var compose = File.ReadAllText(Path.Combine(root, "docker", "compose.yaml"));
        var example = File.ReadAllText(Path.Combine(root, ".env.example"));
        var docs = File.ReadAllText(Path.Combine(root, "docs", "configuration.md"));

        var variables = Variables().Matches(compose)
            .Select(match => match.Groups["name"].Value)
            .ToHashSet();

        Assert.NotEmpty(variables);

        var missing = variables
            .Where(name => !Regex.IsMatch(example, $@"^{name}=", RegexOptions.Multiline))
            .Select(name => $"{name} is not in .env.example")
            .Concat(variables
                .Where(name => !docs.Contains($"`{name}`", StringComparison.Ordinal))
                .Select(name => $"{name} is not in docs/configuration.md"))
            .ToList();

        Assert.True(missing.Count == 0, string.Join("\n", missing));
    }

    /// <summary>
    /// Every code host the application understands can be given its secret in a container.
    /// </summary>
    [RepositoryFact]
    public void Every_code_host_has_its_secret_passed_through()
    {
        var compose = File.ReadAllText(Path.Combine(Root(), "docker", "compose.yaml"));

        foreach (var provider in Enum.GetNames<JiranisokoTech.Domain.Engineering.GitProvider>())
        {
            Assert.Contains($"Git__Providers__{provider}__Secret", compose);
        }
    }

    /// <summary>
    /// The shared environments each have a committed settings file that parses.
    /// </summary>
    /// <remarks>
    /// Not Development. <c>appsettings.Development.json</c> is ignored by git on purpose — it is
    /// where a developer keeps a local connection string — so a test demanding it would fail on
    /// every fresh clone, and a committed one would be overwritten by, or overwrite, somebody's
    /// own. Development's differences are made in code, where the pipeline asks
    /// <c>IsDevelopment</c>.
    /// </remarks>
    [Theory]
    [InlineData("Testing")]
    [InlineData("Staging")]
    [InlineData("Production")]
    public void Each_environment_has_a_settings_file(string environment)
    {
        var path = Path.Combine(Root(), "src", "JiranisokoTech.Web", $"appsettings.{environment}.json");

        Assert.True(File.Exists(path), $"appsettings.{environment}.json is missing");

        using var parsed = JsonDocument.Parse(File.ReadAllText(path));
    }

    /// <summary>
    /// Every copy but the live one says what it is; the live one says nothing.
    /// </summary>
    [Theory]
    [InlineData("Production", false)]
    [InlineData("Staging", true)]
    [InlineData("Testing", true)]
    [InlineData("Development", true)]
    [InlineData("Training", true)]
    public void Only_the_live_system_carries_no_banner(string environment, bool banner) =>
        Assert.Equal(banner, EnvironmentBanner.For(environment) is not null);

    /// <summary><c>${NAME}</c>, <c>${NAME:-default}</c> and <c>${NAME:?message}</c>.</summary>
    [GeneratedRegex(@"\$\{(?<name>[A-Z][A-Z0-9_]*)(?::[-?][^}]*)?\}")]
    private static partial Regex Variables();

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ".env.example")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }
}
