using System.Text.RegularExpressions;
using JiranisokoTech.Application.Authorization;

namespace JiranisokoTech.Tests.Authorization;

/// <summary>
/// Every permission this system declares is enforced by something.
/// </summary>
/// <remarks>
/// This is the test that should have existed several increments ago, and its
/// absence let the same mistake happen four times.
///
/// A permission is easy to add and hard to notice missing. It compiles, the
/// seeder writes it, the role matrix grants it, the matrix tests assert who
/// holds it — and nothing anywhere checks it. Everything passes. What actually
/// happened, found by sweeping for this by hand:
///
/// tasks.update_own, tasks.submit and tasks.review were checked nowhere, while
/// the work item page put every transition behind tasks.assign — so an engineer
/// could open their own work and not start it or submit it. tasks.deploy named
/// a state the state machine does not have at all. time.view_all was granted to
/// HR while the timesheet page asked for time.approve, which HR do not hold, so
/// HR could not see a timesheet. And expenses.approve was checked nowhere, so
/// the separation between approving a claim and paying it — which has a test of
/// its own proving no role holds both — existed in the role matrix and not in
/// the code.
///
/// Four faults, one shape. A test that reads the source is a blunt instrument,
/// and it is the right one here: the thing being asserted is that a string
/// appears somewhere it can do some good, and no amount of running the
/// application proves the absence of a check on a page nobody opened.
/// </remarks>
public class EnforcementTests
{
    /// <summary>
    /// Permissions that are deliberately not checked by any page or endpoint.
    /// </summary>
    /// <remarks>
    /// Empty, and meant to stay that way. It exists so that a genuine exception
    /// has somewhere to go with a reason attached, rather than being achieved
    /// by weakening the test.
    /// </remarks>
    private static readonly Dictionary<string, string> Exempt = [];

    [Fact]
    public void Every_permission_is_checked_somewhere()
    {
        var source = SourceOf("src");
        var unenforced = new List<string>();

        foreach (var (name, value) in Declared())
        {
            if (Exempt.ContainsKey(value))
            {
                continue;
            }

            // Counted outside the file that declares them, because a constant
            // referring to itself proves nothing. Matched on the full path, not
            // the file name: the first version ended with "Permissions.cs",
            // which also excluded WorkPermissions.cs and reported three
            // permissions as unenforced that the file next door enforces.
            var checks = source
                .Where(file => !file.Path.EndsWith(Declaring, StringComparison.Ordinal))
                .Count(file => file.Text.Contains($"Permissions.{name}", StringComparison.Ordinal));

            if (checks == 0)
            {
                unenforced.Add($"{value} ({name})");
            }
        }

        Assert.True(
            unenforced.Count == 0,
            "These permissions are declared and granted to roles, and nothing in the application "
            + "ever checks them. Each one is a capability somebody has been given and cannot use, "
            + "or a restriction that is not actually applied:\n  "
            + string.Join("\n  ", unenforced));
    }

    /// <summary>
    /// Every permission a page asks for is one that exists.
    /// </summary>
    /// <remarks>
    /// The other direction, and it is the cheaper mistake: a policy naming a
    /// permission nothing grants refuses everybody, for ever, silently. It
    /// cannot happen through the constants — that would not compile — but it
    /// can through a policy string written out by hand.
    /// </remarks>
    [Fact]
    public void Every_policy_string_names_a_permission_that_exists()
    {
        var invented = new List<string>();

        foreach (var file in SourceOf("src"))
        {
            foreach (Match match in Regex.Matches(file.Text, @"""permission:([a-z_.]+)"""))
            {
                var named = match.Groups[1].Value;

                if (!Permissions.All.Contains(named))
                {
                    invented.Add($"{Path.GetFileName(file.Path)} asks for \"{named}\"");
                }
            }
        }

        Assert.True(
            invented.Count == 0,
            "These policies name a permission this system does not have, so they refuse "
            + "everybody:\n  " + string.Join("\n  ", invented));
    }

    /// <summary>
    /// Every permission is held by at least one role.
    /// </summary>
    /// <remarks>
    /// One that nothing grants is a feature nobody can reach — the same fault
    /// as the other two, arriving from the third direction.
    /// </remarks>
    [Fact]
    public void Every_permission_is_granted_to_some_role()
    {
        var granted = Roles.Matrix.Values.SelectMany(permissions => permissions).ToHashSet();

        var ungranted = Permissions.All.Where(one => !granted.Contains(one)).ToList();

        Assert.True(
            ungranted.Count == 0,
            "No role holds these, so nothing they guard can be reached by anybody:\n  "
            + string.Join("\n  ", ungranted));
    }

    private static string Declaring { get; } =
        Path.Combine("JiranisokoTech.Application", "Authorization", "Permissions.cs");

    private static IEnumerable<(string Name, string Value)> Declared()
    {
        var path = Path.Combine(SolutionRoot(), "src", Declaring);

        var text = File.ReadAllText(path);

        // Only the ones inside the Permissions class: the Roles class below it
        // declares constants of the same shape that are role names, not
        // permissions, and counting those would demand enforcement of a thing
        // that is not one.
        var permissionsClass = text[..text.IndexOf("public static class Roles", StringComparison.Ordinal)];

        foreach (Match match in Regex.Matches(
            permissionsClass, @"public const string (\w+) = ""([a-z_.]+)"";"))
        {
            yield return (match.Groups[1].Value, match.Groups[2].Value);
        }
    }

    private static List<(string Path, string Text)> SourceOf(string folder) =>
        [.. Directory
            .EnumerateFiles(Path.Combine(SolutionRoot(), folder), "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal)
                || path.EndsWith(".razor", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(path => (path, File.ReadAllText(path)))];

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
