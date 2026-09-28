using System.Text.RegularExpressions;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// No page promises the compiler a value that the first render will not have.
/// </summary>
/// <remarks>
/// <b>Found by opening the security centre on PostgreSQL, after every test had passed.</b> The
/// page held what it read in a property declared <c>= default!</c> and filled it in
/// <c>OnInitializedAsync</c>. The renderer draws a component once as soon as that method first
/// awaits something that has not finished, so on a real database the markup ran with the
/// property still null and the page was an error screen. The tests use SQLite in memory, where
/// the query completes before the await yields, so the early render never happened and no page
/// test could see it.
///
/// <c>default!</c> is exactly the promise that is false here: "this is never null by the time
/// anybody reads it". It is true for what the framework supplies before the first render —
/// injected services, parameters, cascaded values, bound forms — and false for anything a
/// lifecycle method loads. So a <c>default!</c> is allowed only under one of those attributes;
/// anything else should be nullable and the markup should say what to show until it arrives.
/// </remarks>
public partial class RenderedBeforeLoadedTests
{
    private static readonly string[] SuppliedBeforeRender =
    [
        "[Inject]", "[Parameter]", "[CascadingParameter]",
        "[SupplyParameterFromForm", "[SupplyParameterFromQuery",
    ];

    [Fact]
    public void Only_what_the_framework_supplies_is_declared_never_null()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(WebProject(), "*.razor", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);

            for (var i = 0; i < lines.Length; i++)
            {
                if (!PromisedProperty().IsMatch(lines[i]))
                {
                    continue;
                }

                var above = i > 0 ? lines[i - 1].Trim() : string.Empty;

                if (!SuppliedBeforeRender.Any(attribute => above.StartsWith(attribute, StringComparison.Ordinal)))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}  {lines[i].Trim()}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A page renders once before OnInitializedAsync finishes, whenever it awaits real I/O, "
            + "so a property it loads is null in that render whatever default! says. Make it "
            + "nullable and render nothing, or a placeholder, until it is there:\n  "
            + string.Join("\n  ", offenders));
    }

    [GeneratedRegex(@"^\s*(private|protected|public)\s+[\w.<>?,\s]+\s+\w+\s*\{\s*get;\s*(private\s+)?set;\s*\}\s*=\s*default!;")]
    private static partial Regex PromisedProperty();

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
}
