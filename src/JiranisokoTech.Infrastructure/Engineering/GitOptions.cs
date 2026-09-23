using JiranisokoTech.Application.Engineering;
using JiranisokoTech.Domain.Engineering;
using Microsoft.Extensions.Options;

namespace JiranisokoTech.Infrastructure.Engineering;

/// <summary>
/// The webhook secrets, and how hard the dispatcher tries.
/// </summary>
/// <remarks>
/// The secrets are configuration rather than database rows, and that is a
/// deliberate inversion of where the rest of this system keeps things. A
/// database holding working credentials is a database whose backups are working
/// credentials; configuration can come from an environment variable, a mounted
/// file or a vault, and none of those end up in a nightly dump alongside the
/// invoices.
///
/// In development, set it with the secret manager rather than in appsettings:
///
///     dotnet user-secrets set "Git:Providers:GitHub:Secret" "the-value-github-holds"
/// </remarks>
public sealed class GitOptions
{
    public const string Section = "Git";

    /// <summary>The secret per provider, keyed by the provider's name.</summary>
    public Dictionary<string, ProviderOptions> Providers { get; set; } = [];

    /// <summary>How long between passes when the last one found nothing.</summary>
    /// <remarks>
    /// Ten seconds. A push is not an email — nobody is sitting watching for it —
    /// and the board being right ten seconds after a merge instead of instantly
    /// costs nothing, while a tighter loop costs a query against every instance
    /// forever.
    /// </remarks>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How many deliveries one pass handles.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>The wait after a pass that failed outright.</summary>
    public TimeSpan ErrorBackoff { get; set; } = TimeSpan.FromSeconds(30);
}

public sealed class ProviderOptions
{
    public string? Secret { get; set; }
}

/// <summary>
/// The configured secrets, read through the options system.
/// </summary>
/// <remarks>
/// Bound by name rather than by the enum, because configuration keys are
/// strings and Git:Providers:GitHub:Secret is what somebody types. Matched
/// without regard to case, so that GitHub, github and GITHUB all work —
/// a secret that is present but spelt differently would leave the endpoint
/// refusing every delivery for a reason nothing on a screen would explain.
/// </remarks>
public sealed class WebhookSecrets(IOptionsMonitor<GitOptions> options) : IWebhookSecrets
{
    public string? For(GitProvider provider)
    {
        var configured = options.CurrentValue.Providers;

        foreach (var (name, settings) in configured)
        {
            if (string.Equals(name, provider.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return settings.Secret;
            }
        }

        return null;
    }
}
