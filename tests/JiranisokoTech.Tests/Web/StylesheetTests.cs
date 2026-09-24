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

    /// <summary>
    /// A table takes its own overflow rather than the page's.
    /// </summary>
    /// <remarks>
    /// A weak test for a fault that keeps coming back, and it is written down because the
    /// strong version needs a browser. A five-column table wants about 640 pixels and a phone
    /// gives it 375, so without this the table pushes the document wider than the viewport and
    /// the <i>whole page</i> scrolls sideways — heading, navigation and all — while somebody
    /// tries to read one row. Three screens did exactly that, one of them for weeks, and
    /// "a mobile layout overflowing" is already on the list of faults this codebase has paid
    /// for once.
    ///
    /// What this asserts is only that the rule still says so. What actually proves it is
    /// opening the pages at 375 pixels and comparing scrollWidth against clientWidth, which is
    /// how both the fault and the fix were found.
    /// </remarks>
    [Fact]
    public void A_wide_table_scrolls_itself_rather_than_the_page()
    {
        var rule = Regex.Match(TheStylesheet(), @"^\.grid \{([^}]*)\}", RegexOptions.Multiline);

        Assert.True(rule.Success, "There is no .grid rule in app.css any more.");

        Assert.Contains("overflow-x", rule.Groups[1].Value);
    }

    /// <summary>The one stylesheet every page loads.</summary>
    private static string TheStylesheet() =>
        File.ReadAllText(Path.Combine(WebProject(), "wwwroot", "app.css"));

    private static HashSet<string> DefinedClasses(string web)
    {
        var defined = new HashSet<string>();

        foreach (var sheet in Directory.EnumerateFiles(web, "*.css", SearchOption.AllDirectories))
        {
            defined.UnionWith(ClassesDefinedIn(File.ReadAllText(sheet)));
        }

        return defined;
    }

    /// <summary>
    /// No selector is declared twice outside the theme blocks.
    /// </summary>
    /// <remarks>
    /// Two rules for one selector is not a style preference, it is a cascade collision that
    /// nobody can see by reading either half — and this stylesheet has already paid for one.
    ///
    /// <c>.instructions</c> was written for a block of payment details somebody types into a
    /// box, with <c>white-space: pre-wrap</c> so their line breaks survive. It was then reused
    /// as the class for every page's own explanation of itself, eleven of them, all written as
    /// prose in <c>.razor</c> files — and Razor keeps its source's line breaks, so every one of
    /// those paragraphs rendered with the indentation of the file it lives in: an orphaned word
    /// alone on a line, the next line starting four spaces in. A second rule was later added
    /// with a different measure, and the two silently disagreed for a week, with the later one
    /// winning for no reason anybody chose.
    ///
    /// Nothing failed. The class existed, so <c>StylesheetTests</c> was happy; the rule
    /// applied, so the page rendered; and the only way to find it was to open a screen and
    /// read the words on it.
    ///
    /// Media queries and the <c>[data-theme]</c> blocks are excluded, because redeclaring a
    /// selector inside one is how a theme is written rather than an accident.
    /// </remarks>
    [Fact]
    public void No_selector_is_declared_twice()
    {
        var stylesheet = OutsideMediaBlocks(
            Regex.Replace(TheStylesheet(), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline));

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var rule in Regex.Matches(stylesheet, @"([^{}]+)\{[^{}]*\}"))
        {
            var selector = string.Join(
                ' ',
                ((Match)rule).Groups[1].Value.Split(
                    (char[]?)null, StringSplitOptions.RemoveEmptyEntries));

            if (selector.Length == 0 || selector.StartsWith('@'))
            {
                continue;
            }

            counts[selector] = counts.GetValueOrDefault(selector) + 1;
        }

        var twice = counts.Where(one => one.Value > 1).Select(one => $"  {one.Key}").Order();

        Assert.True(
            !twice.Any(),
            "These selectors are declared more than once, so whichever rule happens to sit "
            + "lower in the file wins and neither half says so:\n"
            + string.Join('\n', twice)
            + "\n\nMerge them. If the two rules are for two different jobs, that is two "
            + "classes rather than one — see .instructions and .as-typed.");
    }

    /// <summary>The stylesheet with every media block removed.</summary>
    /// <remarks>
    /// Removed brace-balanced rather than to the first closing brace, because a media query
    /// wraps other rules and a naive cut would stop in the middle of one, leaving a fragment
    /// that reads as a selector.
    /// </remarks>
    private static string OutsideMediaBlocks(string css)
    {
        while (true)
        {
            var start = Regex.Match(css, @"@media[^{]*\{");

            if (!start.Success)
            {
                return css;
            }

            var depth = 0;
            var end = -1;

            for (var i = css.IndexOf('{', start.Index); i < css.Length; i++)
            {
                if (css[i] == '{')
                {
                    depth++;
                }
                else if (css[i] == '}' && --depth == 0)
                {
                    end = i;
                    break;
                }
            }

            if (end < 0)
            {
                return css;
            }

            css = css.Remove(start.Index, end - start.Index + 1);
        }
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
