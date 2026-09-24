using System.Text.RegularExpressions;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// A component used on a page has to be in scope on that page.
/// </summary>
/// <remarks>
/// <b>This exists because of a live fault nothing else was ever going to find.</b> The client
/// page attaches documents through a shared component — <c>&lt;Attached /&gt;</c>, one copy for
/// the five screens that all used to have their own list and upload form. The component lives
/// under <c>Components/Pages/Documents</c>, and <c>_Imports.razor</c> reaches only
/// <c>Components</c> and <c>Components.Layout</c>, so the page never had the using directive
/// that brings it into scope.
///
/// Razor does not fail for that. It decides the tag is an HTML element it has not heard of,
/// emits it as written, and the browser drops it — so the client page's whole "Attached"
/// section was a heading with nothing underneath it, and had been since the shared component
/// was introduced. Every test passed. The page loaded. Nobody could attach a document to a
/// client, and there was no error anywhere to say why.
///
/// The compiler <i>did</i> say so, as warning RZ10012. It went unseen because an incremental
/// build does not recompile what it thinks has not changed, and CLAUDE.md even records RZ10012
/// as a harmless incremental artifact — which it sometimes is, and this time was not. That is
/// exactly the sort of warning that needs a test instead of a rule somebody has to remember:
/// the whole build now runs clean at zero warnings, and this test keeps one specific and
/// silent way of breaking a page out of it.
/// </remarks>
public partial class ComponentsInScopeTests
{
    [Fact]
    public void Every_component_a_page_uses_is_in_scope_on_that_page()
    {
        var web = WebProject();
        var components = Components(web);
        var imported = Imported(web);
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(web, "*.razor", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            var usings = Usings().Matches(text).Select(one => one.Groups["ns"].Value).ToHashSet();
            var here = Namespace(web, file);

            foreach (Match tag in Tags().Matches(Markup(text)))
            {
                var name = tag.Groups["name"].Value;

                if (!components.TryGetValue(name, out var where) || where == here)
                {
                    continue;
                }

                /*
                 * In scope if the page names the namespace itself, or if _Imports does. Two
                 * components of the same name in different folders would make this ambiguous,
                 * and the dictionary below refuses to build in that case rather than guessing
                 * which one a page meant.
                 */
                if (!usings.Contains(where) && !imported.Contains(where))
                {
                    offenders.Add(
                        $"{Path.GetFileName(file)} uses <{name} /> and does not reach {where}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A component that is not in scope is not a compile error. Razor emits the tag as an "
            + "unknown HTML element, the browser drops it, and the page renders with a hole in "
            + "it — which is how the client page lost its whole attachments section without "
            + "anything failing. Add the @using, or move the component:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Every component in the web project, by name, with the namespace it sits in.
    /// </summary>
    /// <remarks>
    /// Routable pages are included as well as shared components, because a page can be rendered
    /// as a component and the fault would look the same. The files the framework generates for
    /// itself — the app shell, the router, the imports — are not components anybody writes a tag
    /// for.
    /// </remarks>
    private static Dictionary<string, string> Components(string web)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(web, "*.razor", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(file);

            if (name is "_Imports" or "App" or "Routes")
            {
                continue;
            }

            var space = Namespace(web, file);

            if (found.TryGetValue(name, out var already) && already != space)
            {
                // Two components with one name is its own problem, and this test is not it.
                // Dropping the name rather than guessing keeps the failure honest.
                found[name] = string.Empty;

                continue;
            }

            found[name] = space;
        }

        return found
            .Where(one => one.Value.Length > 0)
            .ToDictionary(one => one.Key, one => one.Value, StringComparer.Ordinal);
    }

    private static HashSet<string> Imported(string web)
    {
        var imports = Path.Combine(web, "Components", "_Imports.razor");

        return Usings()
            .Matches(File.Exists(imports) ? File.ReadAllText(imports) : string.Empty)
            .Select(one => one.Groups["ns"].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The namespace a component gets from where its file sits.</summary>
    private static string Namespace(string web, string file)
    {
        var folder = Path.GetDirectoryName(Path.GetRelativePath(web, file)) ?? string.Empty;

        return folder.Length == 0
            ? "JiranisokoTech.Web"
            : "JiranisokoTech.Web." + folder
                .Replace(Path.DirectorySeparatorChar, '.')
                .Replace(Path.AltDirectorySeparatorChar, '.');
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

    /// <summary>
    /// The markup half of a Razor file.
    /// </summary>
    /// <remarks>
    /// Everything before <c>@code</c>, because the C# below it is full of things that look
    /// exactly like a component tag and are not. The first version of this test read the whole
    /// file and reported <c>List&lt;Review&gt;</c> in a field declaration as a page using an
    /// incident-page component — which is the sort of false positive that gets a meta-test
    /// deleted rather than fixed.
    ///
    /// The tag pattern also refuses a <c>&lt;</c> that follows an identifier character, so a
    /// generic inside markup — an <c>@typeparam</c>, a cast — is not mistaken for a tag either.
    /// </remarks>
    private static string Markup(string text)
    {
        var code = text.IndexOf("@code", StringComparison.Ordinal);

        return code < 0 ? text : text[..code];
    }

    /// <summary>A tag whose name starts with a capital, which is how Razor spots a component.</summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9_])<(?<name>[A-Z][A-Za-z0-9_]*)(?=[\s/>])")]
    private static partial Regex Tags();

    [GeneratedRegex(@"^@using\s+(?<ns>[\w\.]+)\s*$", RegexOptions.Multiline)]
    private static partial Regex Usings();
}
