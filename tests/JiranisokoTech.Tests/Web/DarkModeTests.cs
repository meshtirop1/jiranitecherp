using System.Text.RegularExpressions;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// Every colour the stylesheet sets is a colour that works in both themes.
/// </summary>
/// <remarks>
/// The sibling of <see cref="StylesheetTests"/>, and it exists because that one cannot
/// catch this. StylesheetTests asks whether a class is DEFINED, which is the fault that
/// renders a page as an unstyled column of text. A colour is the other kind: the class is
/// there, the rule is there, the page looks right — in one theme.
///
/// An audit of app.css found ninety-seven rules setting a colour as a hex literal, of which
/// sixty-seven had no dark-mode override at all. Among them were <c>button, .button</c>, so
/// every button in the application drew a light-theme blue-on-white against a dark page, and
/// <c>.validation-message</c>, so the sentence telling somebody what they had typed wrongly
/// was a red chosen to sit on white. Nothing failed. Nothing could have: the dark theme is
/// only ever seen by somebody whose machine is set to it, and the people who built these
/// pages were not.
///
/// The fix was to promote those colours to tokens, so that dark mode is one redefinition of
/// a value rather than a second copy of a rule. This test is what stops the next one being
/// written as a literal.
/// </remarks>
public class DarkModeTests
{
    /// <summary>
    /// Colours that are deliberately the same in both themes, with the reason.
    /// </summary>
    /// <remarks>
    /// One entry, and it should stay that way. Anything added here is a rule somebody has
    /// decided the reader's theme does not get a vote on, which is almost never true — so the
    /// reason has to be a fact about the world rather than a preference.
    /// </remarks>
    private static readonly Dictionary<string, string> SameInBothThemes = new()
    {
        [".qr svg"] =
            "A QR code is read by a camera looking for dark modules on a light ground. Drawn "
            + "on a dark surface it does not scan, so the theme does not get a vote.",
    };

    private static readonly Regex Literal = new(@"#[0-9a-fA-F]{3,8}\b", RegexOptions.Compiled);

    /// <summary>
    /// A colour written as a literal is a colour that only works in one theme.
    /// </summary>
    /// <remarks>
    /// Tokens are exempt by construction: the four <c>:root</c> blocks are where a literal
    /// belongs, because that is the one place a value is chosen per theme rather than per
    /// rule.
    /// </remarks>
    [Fact]
    public void Every_colour_outside_the_token_blocks_reads_a_token()
    {
        var stylesheet = File.ReadAllText(
            Path.Combine(WebProject(), "wwwroot", "app.css"));

        var offenders = new List<string>();

        foreach (var (selector, properties) in ColourRules(stylesheet))
        {
            if (SameInBothThemes.ContainsKey(selector))
            {
                continue;
            }

            offenders.Add($"  {selector} sets {string.Join(", ", properties)} as a literal");
        }

        Assert.True(
            offenders.Count == 0,
            "These rules choose a colour with a hex literal, so the reader's theme cannot "
            + "change it — which in practice means they were written against the light theme "
            + "and are wrong on a dark page:\n"
            + string.Join('\n', offenders.Order())
            + "\n\nRead a token instead (--ink, --danger, --good-ink, …), or add the selector "
            + "to SameInBothThemes with a reason that is a fact about the world.");
    }

    /// <summary>
    /// Every token the light theme declares is declared for dark as well.
    /// </summary>
    /// <remarks>
    /// The other half, and the one that bites when somebody adds a token. A value declared
    /// only in the bare <c>:root</c> block silently keeps its light value in dark mode, which
    /// is the same fault as a literal wearing a token's clothes — and harder to see, because
    /// the rule that reads it looks correct.
    /// </remarks>
    [Fact]
    public void Every_token_is_declared_for_dark_as_well_as_light()
    {
        var stylesheet = File.ReadAllText(
            Path.Combine(WebProject(), "wwwroot", "app.css"));

        var light = Tokens(RootBlock(stylesheet, @"^:root \{"));
        var media = Tokens(RootBlock(stylesheet, @":root:not\(\[data-theme=""light""\]\) \{"));
        var stamped = Tokens(RootBlock(stylesheet, @"^:root\[data-theme=""dark""\] \{"));

        Assert.NotEmpty(light);

        var missingFromMedia = light.Except(media).Order().ToList();
        var missingFromStamped = light.Except(stamped).Order().ToList();

        Assert.True(
            missingFromMedia.Count == 0,
            "These tokens are declared for the light theme and not inside the "
            + "prefers-color-scheme block, so a reader whose machine is set to dark gets the "
            + "light value:\n  " + string.Join("\n  ", missingFromMedia));

        Assert.True(
            missingFromStamped.Count == 0,
            "These tokens are missing from :root[data-theme=\"dark\"], so a reader who chose "
            + "dark on a light machine gets the light value:\n  "
            + string.Join("\n  ", missingFromStamped));
    }

    /// <summary>The token names declared inside one block.</summary>
    private static HashSet<string> Tokens(string block) =>
        [.. Regex.Matches(block, @"(--[a-z-]+)\s*:").Select(one => one.Groups[1].Value)];

    /// <summary>
    /// One brace-balanced block, found by its opening selector.
    /// </summary>
    /// <remarks>
    /// Balanced rather than read to the first closing brace, because the media query wraps
    /// another block and a naive read would stop halfway through the tokens it declares —
    /// reporting half the file as missing them.
    /// </remarks>
    private static string RootBlock(string stylesheet, string opening)
    {
        var start = Regex.Match(stylesheet, opening, RegexOptions.Multiline);

        Assert.True(start.Success, $"No block matching {opening}");

        var depth = 0;

        for (var i = stylesheet.IndexOf('{', start.Index); i < stylesheet.Length; i++)
        {
            if (stylesheet[i] == '{')
            {
                depth++;
            }
            else if (stylesheet[i] == '}' && --depth == 0)
            {
                return stylesheet[start.Index..i];
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Every rule outside the token blocks that sets a colour as a literal.
    /// </summary>
    /// <remarks>
    /// Comments are stripped first. This file explains its colour decisions in prose and
    /// names the hex values it moved away from, so a reader that counted those would report
    /// the explanation as the fault.
    /// </remarks>
    private static IEnumerable<(string Selector, IReadOnlyList<string> Properties)> ColourRules(
        string stylesheet)
    {
        var bare = Regex.Replace(stylesheet, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

        foreach (var span in TokenBlocks(bare).OrderByDescending(one => one.Start))
        {
            bare = bare.Remove(span.Start, span.Length);
        }

        foreach (var rule in Regex.Matches(bare, @"([^{}]+)\{([^{}]*)\}"))
        {
            var match = (Match)rule;
            var selector = string.Join(' ', match.Groups[1].Value.Split(
                (char[]?)null, StringSplitOptions.RemoveEmptyEntries));

            var properties = Regex
                .Matches(
                    match.Groups[2].Value,
                    @"(background|background-color|color|border[a-z-]*|box-shadow|outline[a-z-]*)\s*:\s*([^;]*)")
                .Where(one => Literal.IsMatch(one.Groups[2].Value))
                .Select(one => one.Groups[1].Value)
                .Distinct()
                .ToList();

            if (properties.Count > 0)
            {
                yield return (selector, properties);
            }
        }
    }

    /// <summary>Where the token blocks are, so their literals are left alone.</summary>
    private static List<(int Start, int Length)> TokenBlocks(string bare)
    {
        var spans = new List<(int Start, int Length)>();

        foreach (var opening in new[]
        {
            @"@media \(prefers-color-scheme: dark\)\s*\{",
            @":root\[data-theme=""dark""\]\s*\{",
            @"^:root \{",
        })
        {
            foreach (var found in Regex.Matches(bare, opening, RegexOptions.Multiline))
            {
                var match = (Match)found;
                var depth = 0;

                for (var i = bare.IndexOf('{', match.Index); i < bare.Length; i++)
                {
                    if (bare[i] == '{')
                    {
                        depth++;
                    }
                    else if (bare[i] == '}' && --depth == 0)
                    {
                        spans.Add((match.Index, i - match.Index + 1));
                        break;
                    }
                }
            }
        }

        return spans;
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
}
