using System.Text.RegularExpressions;

namespace JiranisokoTech.Tests.Api;

/// <summary>
/// Every permission the public API asks for can be put on a key.
/// </summary>
/// <remarks>
/// <b>This exists because of a door no key could open.</b> Section 68 added an endpoint at
/// /api/v1/flags behind platform.view, and the screen that issues API keys offers a hand-written
/// list of four scopes that did not include it. The endpoint was live, correct and
/// unreachable — no key could hold the claim it required, and nothing anywhere said so.
///
/// EnforcementTests did not catch it, and is right not to: platform.view is checked, on three
/// screens. The gap is between two lists that have to agree and are maintained in different
/// files by different changes, which is the shape of fault a meta-test is for.
///
/// Read out of the source rather than from the endpoint table, because the endpoints are built
/// inside a method that needs a whole application to call — and because the list this is
/// checking against is source too. Two lists, both read as text, compared.
/// </remarks>
public partial class ApiScopeTests
{
    [Fact]
    public void Every_permission_the_api_wants_can_be_put_on_a_key()
    {
        var web = WebProject();

        var api = File.ReadAllText(Path.Combine(web, "Api", "PublicApi.cs"));
        var keys = File.ReadAllText(
            Path.Combine(web, "Components", "Pages", "ApiKeys.razor"));

        var wanted = Required().Matches(api)
            .Select(match => match.Groups["permission"].Value)
            .Distinct()
            .ToList();

        Assert.NotEmpty(wanted);

        var block = keys[keys.IndexOf(
            "private static readonly string[] Offered", StringComparison.Ordinal)..];

        block = block[..block.IndexOf("];", StringComparison.Ordinal)];

        /*
         * Commented-out lines are dropped before anything is matched, and that is not fussiness:
         * the first version of this test searched the block as one string, so commenting a
         * permission out left it passing — which was discovered by deliberately commenting one
         * out to watch the test fail and watching it go green instead. A test that cannot fail
         * is worse than no test, because it is counted.
         */
        var offered = block
            .Split(Environment.NewLine.ToCharArray())
            .Select(line => line.Trim())
            .Where(line => !line.StartsWith("//", StringComparison.Ordinal))
            .ToList();

        var missing = wanted
            .Where(permission => !offered.Any(line => line.Contains(
                "Permissions." + permission + ",", StringComparison.Ordinal)))
            .ToList();

        Assert.True(
            missing.Count == 0,
            "The public API asks for permissions that cannot be put on an API key, so those "
            + "endpoints are doors no key can open:\n  "
            + string.Join("\n  ", missing)
            + "\nAdd them to the Offered list on the API keys screen, or take the endpoint out.");
    }

    private static string WebProject()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null
            && !Directory.Exists(Path.Combine(directory.FullName, "src", "JiranisokoTech.Web")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return Path.Combine(directory!.FullName, "src", "JiranisokoTech.Web");
    }

    /// <summary>A <c>.RequirePermission(Permissions.Something)</c> call.</summary>
    [GeneratedRegex(@"RequirePermission\(Permissions\.(?<permission>\w+)\)")]
    private static partial Regex Required();
}
