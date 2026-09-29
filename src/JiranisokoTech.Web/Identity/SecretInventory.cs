using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;

namespace JiranisokoTech.Web.Identity;

/// <summary>
/// Every secret this application reads, whether it is set, and where it came from. Never what
/// it is.
/// </summary>
/// <remarks>
/// Section 28's secret references. The values are in <c>.env</c> on the host, passed to the
/// container as environment variables; this is the list of them an administrator can check
/// without a shell on the server.
///
/// <b>Where a value came from is the useful half.</b> "Set" alone would be green for a mail
/// password typed into <c>appsettings.json</c> and committed, which is the one way of setting a
/// secret that is always wrong. So each value is traced to the configuration provider that
/// supplied it, and one that arrived from a settings file is shown as a problem.
///
/// The value itself is never read into anything that leaves this class — not a length, not a
/// prefix. A page that showed the first four characters "to help recognise it" would be a
/// page that leaks four characters of every secret to anybody who can open it.
/// </remarks>
public sealed class SecretInventory(IConfiguration configuration)
{
    /// <summary>What is read, and what each one is for, in words.</summary>
    private static readonly (string Key, string What, bool Temporary)[] Known =
    [
        ("ConnectionStrings:Default", "Database connection, including its password", false),
        ("Mail:Password", "Mail server password", false),
        ("Git:Providers:GitHub:Secret", "GitHub webhook signing secret", false),
        ("Git:Providers:GitLab:Secret", "GitLab webhook token", false),
        ("Git:Providers:Bitbucket:Secret", "Bitbucket webhook signing secret", false),
        ("Git:Providers:AzureDevOps:Secret", "Azure DevOps webhook secret", false),
        ("Metrics:Token", "Token a metrics collector presents", false),

        // Needed once, to create the first account, and a standing risk after that: anybody
        // who can read the host's environment can read the owner's first password.
        ("Bootstrap:OwnerPassword", "First owner's starting password", true),
    ];

    public IReadOnlyList<SecretReference> All()
    {
        var providers = configuration is IConfigurationRoot root
            ? root.Providers.Reverse().ToList()
            : [];

        return Known.Select(known =>
        {
            var source = providers.FirstOrDefault(provider =>
                provider.TryGet(known.Key, out var value) && !string.IsNullOrEmpty(value));

            return new SecretReference(known.Key, known.What, source is not null, Describe(source), known.Temporary);
        }).ToList();
    }

    private static SourceKind Describe(IConfigurationProvider? provider) => provider switch
    {
        null => SourceKind.None,
        EnvironmentVariablesConfigurationProvider => SourceKind.Environment,
        JsonConfigurationProvider => SourceKind.SettingsFile,
        _ when provider.GetType().Name.Contains("UserSecrets", StringComparison.Ordinal)
            || provider.ToString()?.Contains("secrets.json", StringComparison.Ordinal) == true =>
            SourceKind.DeveloperSecrets,
        _ => SourceKind.Other,
    };
}

public enum SourceKind
{
    None,
    Environment,
    SettingsFile,
    DeveloperSecrets,
    Other,
}

public sealed record SecretReference(
    string Key, string What, bool IsSet, SourceKind Source, bool Temporary)
{
    /// <summary>
    /// Something about this one needs doing.
    /// </summary>
    /// <remarks>
    /// A secret in a settings file is committed to the repository. The owner's starting
    /// password is meant to be cleared after the first sign-in, and one still present is a
    /// password anybody with the host's environment can read.
    /// </remarks>
    public bool NeedsAttention => IsSet && (Source == SourceKind.SettingsFile || Temporary);

    public string Where => Source switch
    {
        SourceKind.None => "Not set",
        SourceKind.Environment => "Environment (.env on the host)",
        SourceKind.SettingsFile => "A settings file — committed with the code",
        SourceKind.DeveloperSecrets => "Developer secrets on this machine",
        _ => "Other configuration",
    };
}
