using System.Text.RegularExpressions;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// A link in the navigation is gated by the permission the page behind it requires.
/// </summary>
/// <remarks>
/// <c>NavMenu</c> states the rule in its own comment — "each link is behind the permission the
/// page itself checks, so nobody is shown a door that will refuse them" — and nothing enforced
/// it, so it stopped being true in both directions at once.
///
/// <b>The board was the expensive half.</b> <c>Work/Board.razor</c> requires
/// <c>tasks.view_own</c>, and <c>tasks.view_own</c> exists for precisely one reason, written on
/// the permission itself: "Separate from view_all so that an engineer can open the board at
/// all. Without it the only people who could see any work were the ones who could see
/// everybody's, which in a delivery system means the people doing the work cannot look at it."
/// The navigation gated the link on <c>tasks.view_all</c> anyway. So five roles — developer, QA
/// engineer, DevOps engineer, designer and support — could open the board and were shown no way
/// to reach it from any page in the application, which also meant no entry in the command
/// palette, because the palette is built out of the sidebar.
///
/// <b>And the auditor was shown a door that refused them.</b> They hold <c>tasks.view_all</c>
/// without <c>tasks.view_own</c>, so the link appeared and <c>/work</c> sent them to
/// <c>/denied</c>.
///
/// Neither was visible to <c>BusinessPageTests.Every_link_a_developer_is_shown_opens</c>,
/// which walks the links a developer <em>is</em> shown: a link that is absent opens nothing to
/// complain about, and the auditor is not the role it signs in as. The mismatch is a fact about
/// two files, so it is checked as one.
/// </remarks>
public partial class NavigationMatchesTheDoorTests
{
    [Fact]
    public void Every_link_is_behind_the_permission_its_page_requires()
    {
        var web = WebProject();
        var navigation = File.ReadAllText(
            Path.Combine(web, "Components", "Layout", "NavMenu.razor"));

        var policies = PolicyByRoute(web);
        var complaints = new List<string>();

        foreach (var (href, guardedBy) in LinksIn(navigation))
        {
            /*
             * A link to a route this scan did not find is not a failure. The navigation points
             * at a handful of things that are not pages with a @page directive — the sign-out
             * form among them — and a test that failed for those would be a test somebody
             * turns off rather than one they fix.
             */
            if (!policies.TryGetValue(href, out var required))
            {
                continue;
            }

            if (guardedBy == required)
            {
                continue;
            }

            complaints.Add(
                $"/{href} is behind {Said(guardedBy)} in the navigation and requires "
                + $"{Said(required)} to open.");
        }

        Assert.True(
            complaints.Count == 0,
            "The navigation shows a link behind one permission to a page that asks for "
            + "another. Whichever way round it is, somebody is worse off: gated more tightly "
            + "than the page and the people who may use it are never shown the way, which is "
            + "how five roles lost the board; gated more loosely and they are shown a door "
            + "that sends them to /denied.\n  "
            + string.Join("\n  ", complaints));
    }

    /// <summary>
    /// Every link in the navigation, with the permission guarding it.
    /// </summary>
    /// <remarks>
    /// Walked by position in the file rather than line by line, and the first version of this
    /// was written line by line and reported every link as ungated. An AuthorizeView here is
    /// routinely written across two lines — the policy on the first and <c>Context</c> and the
    /// closing bracket on the second — so a pattern anchored to one line never matched an
    /// opening tag and the stack stayed empty. The test failed loudly rather than quietly,
    /// which is the only reason it was caught in the same minute.
    ///
    /// The outermost AuthorizeView names no policy; it asks only for a sign-in, and a link
    /// inside nothing else is correctly reported as ungated.
    /// </remarks>
    private static List<(string Href, string? GuardedBy)> LinksIn(string navigation)
    {
        var marks = Opens().Matches(navigation)
            .Select(one => (one.Index, Kind: "open", Value: Names().Match(one.Value) is
                { Success: true } named ? named.Groups["policy"].Value : null))
            .Concat(Closes().Matches(navigation)
                .Select(one => (one.Index, Kind: "close", Value: (string?)null)))
            .Concat(Link().Matches(navigation)
                .Select(one => (one.Index, Kind: "link", Value: (string?)one.Groups["href"].Value)))
            .OrderBy(one => one.Index)
            .ToList();

        var open = new Stack<string?>();
        var links = new List<(string, string?)>();

        foreach (var mark in marks)
        {
            switch (mark.Kind)
            {
                case "open":
                    open.Push(mark.Value);
                    break;

                case "close" when open.Count > 0:
                    open.Pop();
                    break;

                case "link" when mark.Value is { } href && href.Trim('/').Length > 0:
                    links.Add((href.Trim('/'), open.FirstOrDefault(one => one is not null)));
                    break;
            }
        }

        return links;
    }

    /// <summary>The permission each routable page asks for, by its route.</summary>
    /// <remarks>
    /// Only routes with no parameters in them. A link in a navigation never points at
    /// <c>/work/{id:guid}</c>, and trying to match one would mean this test guessing at a route
    /// template rather than comparing two strings.
    /// </remarks>
    private static Dictionary<string, string?> PolicyByRoute(string web)
    {
        var found = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var page in Directory.EnumerateFiles(
            Path.Combine(web, "Components", "Pages"), "*.razor", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(page);
            var route = Route().Match(text);

            if (!route.Success)
            {
                continue;
            }

            var path = route.Groups["route"].Value.Trim('/');

            if (path.Contains('{', StringComparison.Ordinal))
            {
                continue;
            }

            /*
             * An anonymous page is not recorded at all rather than recorded as ungated. The
             * careers pages are reachable by strangers and are not in the navigation, and a
             * null for them would read the same as "signed in, no permission" — which is a
             * different thing and the one the sign-out link is.
             */
            if (text.Contains("[AllowAnonymous]", StringComparison.Ordinal))
            {
                continue;
            }

            var policy = Policy().Match(text);

            found[path] = policy.Success ? policy.Groups["permission"].Value : null;
        }

        return found;
    }

    private static string Said(string? permission) =>
        permission is null ? "no permission" : $"Permissions.{permission}";

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

    /// <summary>
    /// An AuthorizeView opening tag, whole.
    /// </summary>
    /// <remarks>
    /// The tag is matched first and read second, rather than one pattern trying to do both.
    /// The combined version was written first and reported every link as ungated: with the
    /// policy part optional and the parts around it lazy, the engine skips the optional group
    /// and lets the tail consume the attributes, so the capture never fires and the test says
    /// "no permission" about a file full of them. A pattern that can quietly match the wrong
    /// thing is worse in a meta-test than anywhere else, because what it produces is a list of
    /// plausible complaints.
    ///
    /// Singleline so the dot crosses a newline, since the attributes of one tag here are
    /// routinely split over two lines.
    /// </remarks>
    [GeneratedRegex(@"<AuthorizeView(?:(?!>).)*?>", RegexOptions.Singleline)]
    private static partial Regex Opens();

    /// <summary>The permission an opening tag names, if it names one.</summary>
    [GeneratedRegex(
        @"Policy\s*=\s*""@\(PermissionClaim\.PolicyPrefix\s*\+\s*Permissions\.(?<policy>\w+)\)""",
        RegexOptions.Singleline)]
    private static partial Regex Names();

    [GeneratedRegex(@"</AuthorizeView>", RegexOptions.None)]
    private static partial Regex Closes();

    /// <summary>A NavLink, and where it points.</summary>
    [GeneratedRegex(@"<NavLink[^>]*\shref=""(?<href>[^""]*)""", RegexOptions.None)]
    private static partial Regex Link();

    [GeneratedRegex(@"^@page\s+""(?<route>[^""]+)""", RegexOptions.Multiline)]
    private static partial Regex Route();

    [GeneratedRegex(
        @"\[Authorize\(Policy\s*=\s*PermissionClaim\.PolicyPrefix\s*\+\s*Permissions\.(?<permission>\w+)\)\]",
        RegexOptions.None)]
    private static partial Regex Policy();
}
