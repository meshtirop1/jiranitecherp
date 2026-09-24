using System.Text.RegularExpressions;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// What the layout is allowed to do, and why it is less than a page.
/// </summary>
/// <remarks>
/// <b>This exists because of a live fault that only appeared some of the time.</b> The
/// navigation was given a count of open incidents — a good idea, since an open incident is the
/// one thing that should be visible from every page — by injecting a query class straight into
/// <c>NavMenu</c>. Blazor initialises components concurrently, so that query ran at the same
/// moment as whatever the page was reading, on the same scoped <c>AppDbContext</c>, and a
/// DbContext refuses a second operation while one is in flight.
///
/// The result was an error screen on whichever component lost the race. It worked three times
/// and then took a page down, which is the worst way for a fault to behave: the obvious
/// conclusion from the first three attempts is that the feature works.
///
/// A page may inject whatever it likes — it is one component with one unit of work. The layout
/// renders <i>beside</i> every page rather than instead of one, so anything it reads has to come
/// from a scope of its own.
/// </remarks>
public partial class LayoutTests
{
    /// <summary>
    /// Nothing in the layout injects something that reads the database.
    /// </summary>
    /// <remarks>
    /// Matched on the shape of the name rather than on the real types, because this has to fail
    /// for a class that does not exist yet — the next person adding a count to the navigation is
    /// the person this test is written for, and they will not have read the remarks on
    /// NavMenu.OnInitializedAsync until it tells them to.
    /// </remarks>
    [Fact]
    public void The_layout_reads_nothing_through_an_injected_query_or_service()
    {
        var offenders = new List<string>();

        foreach (var component in Directory.EnumerateFiles(
            Path.Combine(WebProject(), "Components", "Layout"),
            "*.razor",
            SearchOption.AllDirectories))
        {
            foreach (Match injected in Injects().Matches(File.ReadAllText(component)))
            {
                var type = injected.Groups["type"].Value;

                if (type.EndsWith("Queries", StringComparison.Ordinal)
                    || type.EndsWith("Service", StringComparison.Ordinal)
                    || type.EndsWith("Repository", StringComparison.Ordinal)
                    || type.EndsWith("AppDbContext", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(component)} injects {type}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "The layout renders beside every page, and Blazor initialises components "
            + "concurrently — so anything here that reads the database does it on the same "
            + "scoped DbContext as the page, which refuses a second operation while one is in "
            + "flight. The failure is an intermittent error screen on whichever one loses. Take "
            + "a scope of your own with IServiceScopeFactory, as NavMenu does:\n  "
            + string.Join("\n  ", offenders));
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

    /// <summary>An <c>@inject</c> line, with whatever type it names.</summary>
    [GeneratedRegex(@"^@inject\s+(?<type>[\w\.<>]+)\s", RegexOptions.Multiline)]
    private static partial Regex Injects();
}
