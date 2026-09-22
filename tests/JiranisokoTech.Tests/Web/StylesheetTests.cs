using System.Text.RegularExpressions;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// Every class a page asks for is a class some stylesheet defines.
/// </summary>
/// <remarks>
/// A page that names a class nothing defines does not fail, warn, or look
/// broken in any way a compiler or a unit test can see. It renders — as an
/// unstyled column of text. This system has already shipped one such page, the
/// sign-in screen, and the only reason it was caught is that somebody opened it
/// in a browser. This is that check, made cheap enough to run every time.
///
/// It reads the markup rather than the rendered output on purpose: a class in a
/// branch that only renders for one role, or only when a list is empty, is
/// exactly the one nobody opens and nobody notices.
/// </remarks>
public class StylesheetTests
{
    /// <summary>
    /// Classes that are switched on by code rather than written in the markup,
    /// or that belong to a library.
    /// </summary>
    private static readonly HashSet<string> NotOurs =
    [
        // Blazor writes these itself around form fields.
        "valid", "invalid", "modified", "validation-message", "validation-errors",
    ];

    [Fact]
    public void Every_class_a_page_uses_is_defined_somewhere()
    {
        var web = WebProject();
        var defined = DefinedClasses(web);
        var missing = new List<string>();

        foreach (var page in Directory.EnumerateFiles(
            Path.Combine(web, "Components"), "*.razor", SearchOption.AllDirectories))
        {
            var markup = File.ReadAllText(page);

            foreach (var name in ClassesIn(markup))
            {
                if (!defined.Contains(name) && !NotOurs.Contains(name))
                {
                    missing.Add($"{Path.GetFileName(page)} uses .{name}");
                }
            }
        }

        Assert.True(
            missing.Count == 0,
            "These classes are used but nothing defines them, so those parts of the "
            + "page render unstyled:\n  " + string.Join("\n  ", missing.Distinct()));
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

    private static HashSet<string> DefinedClasses(string web)
    {
        var defined = new HashSet<string>();

        foreach (var sheet in Directory.EnumerateFiles(web, "*.css", SearchOption.AllDirectories))
        {
            defined.UnionWith(ClassesDefinedIn(File.ReadAllText(sheet)));
        }

        return defined;
    }

    /// <remarks>
    /// Comments are stripped first. A class named in a comment — "not .row,
    /// because Bootstrap already means something by it" — is being warned
    /// about, not defined, and counting it would let a genuinely missing class
    /// through on the strength of a note explaining its absence.
    /// </remarks>
    private static HashSet<string> ClassesDefinedIn(string css) =>
        Regex.Matches(
                Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline),
                @"\.(-?[_a-zA-Z][_a-zA-Z0-9-]*)")
            .Select(match => match.Groups[1].Value)
            .ToHashSet();

    /// <summary>
    /// The class names written literally in a page's markup.
    /// </summary>
    /// <remarks>
    /// Values holding an <c>@</c> are skipped: those are computed, and the
    /// helper that computes them returns names this same check will find where
    /// they are written down. Reading a half-interpolated string as a class name
    /// would report nonsense.
    /// </remarks>
    private static IEnumerable<string> ClassesIn(string markup) =>
        Regex.Matches(markup, @"class=""([^""]*)""")
            .Select(match => match.Groups[1].Value)
            .Where(value => !value.Contains('@'))
            .SelectMany(value => value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Distinct();
}
