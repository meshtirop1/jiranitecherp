using System.Text.RegularExpressions;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// A switch arm in a Razor file must not begin with a less-than pattern.
/// </summary>
/// <remarks>
/// <b>This exists because the sprint planning page stopped compiling on a newer SDK.</b> Its
/// countdown read <c>days switch { &lt; 0 =&gt; "ran out", … }</c> inside the page's code
/// block. The SDK it was written against builds that cleanly; the Razor parser in 10.0.112
/// takes a line that opens with <c>&lt;</c> for the start of an HTML element, and from there
/// reads the rest of the code block as markup and reports 76 errors, none of them on
/// the line that caused it.
///
/// Nothing pinned the SDK, and the Dockerfile builds from the floating
/// <c>sdk:10.0-noble</c> tag, so once that tag carried such an SDK the image would have
/// refused to build — in the image only, with every local build still green. Pinning the
/// SDK would only move the day that happens to whenever somebody raises the pin; the construct
/// is the fragile thing. <c>_ when days &lt; 0</c> means the same and parses the same
/// everywhere. The same test found a second one on the approvals page, which the newer parser
/// happened to tolerate because an earlier arm had already put it back into C#.
/// </remarks>
public partial class RelationalPatternTests
{
    [Fact]
    public void No_line_in_a_razor_file_opens_with_a_less_than_pattern()
    {
        var web = WebProject();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(web, "*.razor", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);

            for (var i = 0; i < lines.Length; i++)
            {
                if (OpensWithLessThanPattern().IsMatch(lines[i]))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}  {lines[i].Trim()}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A line opening with '<' followed by a number is read as an HTML element by some "
            + "versions of the Razor parser, which then treats the rest of the code block as "
            + "markup and fails the build with errors nowhere near the cause. Write the arm as "
            + "'_ when value < n =>' instead:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// <c>&lt; 0</c>, <c>&lt;0</c>, <c>&lt;= 3</c>, <c>&lt; -1</c> at the start of a line.
    /// </summary>
    /// <remarks>
    /// An HTML element name cannot begin with a digit, a sign or an equals sign, so this cannot
    /// match real markup.
    /// </remarks>
    [GeneratedRegex(@"^\s*<=?\s*[-+]?[0-9]")]
    private static partial Regex OpensWithLessThanPattern();

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
