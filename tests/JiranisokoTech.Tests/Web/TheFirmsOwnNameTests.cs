using System.Text.RegularExpressions;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// The firm's name is the firm's to change.
/// </summary>
/// <remarks>
/// <b>This exists because the settings screen promised something it did not deliver.</b>
///
/// <c>FirmSettings.TradingName</c> is editable on <c>/settings</c>, it is on every invoice and
/// every offer letter, and it is the field a firm changes when it renames itself. Seven places in
/// the web project had that name typed into them as a literal instead: both anonymous layouts,
/// four page titles, and an offer letter's default. So renaming the firm renamed it on the paper
/// and on nothing a visitor ever sees — and the two pages an outsider actually looks at, the
/// sign-in panel and the careers site, were the two furthest from the truth.
///
/// Found by pointing the application at a demonstration database and looking at it. The seed
/// calls the firm "Jiranisoko Tech Solutions (DEMONSTRATION DATA)" precisely so that a copy left
/// running cannot be mistaken for the real one, and the careers page went on advertising a
/// real-looking job for a real-looking firm.
///
/// Reading source is the right instrument here for the reason <c>ReachabilityTests</c> gives: no
/// amount of running the application proves the absence of a literal on a page nobody opened.
/// </remarks>
public class TheFirmsOwnNameTests
{
    /// <summary>
    /// Places allowed to say the name outright, with the reason.
    /// </summary>
    /// <remarks>
    /// Two, and they are the two that cannot read a database. A fallback has to have something to
    /// fall back to, and a document's metadata is written before anything is queried.
    /// </remarks>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["Components/Layout/WhoWeAre.razor"] =
            "The component that reads the real one. Its fallback renders for the frame before the "
            + "read completes, and a blank footer for one frame looks like a broken page.",

        ["Components/App.razor"] =
            "The document shell, which is written before any component has run and has no scope "
            + "to read anything from.",
    };

    [Fact]
    public void No_page_or_layout_types_the_firms_name_instead_of_reading_it()
    {
        var web = Path.Combine(SolutionRoot(), "src", "JiranisokoTech.Web");

        var offenders = new List<string>();

        foreach (var path in Directory
            .EnumerateFiles(web, "*.razor", SearchOption.AllDirectories)
            .Where(one => !one.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !one.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var relative = Path.GetRelativePath(web, path).Replace('\\', '/');

            if (Allowed.ContainsKey(relative))
            {
                continue;
            }

            var text = File.ReadAllText(path);

            /*
             * Comments are exempt, and deliberately: this file's own explanation names the firm,
             * and so do several remarks that would otherwise have to talk around it. What is being
             * looked for is the name rendering on a page, which only happens outside a comment.
             */
            var code = Regex.Replace(text, @"@\*.*?\*@", string.Empty, RegexOptions.Singleline);
            code = Regex.Replace(code, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            code = Regex.Replace(code, @"(?m)^\s*//.*$", string.Empty);
            code = Regex.Replace(code, @"(?m)^\s*///.*$", string.Empty);

            foreach (Match found in Regex.Matches(code, @"Jiranisoko\s+Tech\s+Solutions"))
            {
                var line = code.Take(found.Index).Count(one => one == '\n') + 1;

                offenders.Add($"  {relative}:{line}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These render the firm's name as a literal, so changing it on the settings screen "
            + "changes it on invoices and not here:\n"
            + string.Join('\n', offenders.Order())
            + "\n\nUse <WhoWeAre />, which reads FirmSettings.TradingName in a scope of its own. "
            + "If a place genuinely cannot read one, add it to Allowed with the reason.");
    }

    /// <summary>
    /// Nothing in the exemption list has stopped being a file.
    /// </summary>
    /// <remarks>
    /// An exemption outliving the thing it exempts is how a list like this turns into a place
    /// where things go to be forgotten — the same guard ReachabilityTests carries on its own.
    /// </remarks>
    [Fact]
    public void The_exemption_list_has_nothing_stale_in_it()
    {
        var web = Path.Combine(SolutionRoot(), "src", "JiranisokoTech.Web");

        var missing = Allowed.Keys
            .Where(one => !File.Exists(Path.Combine(web, one.Replace('/', Path.DirectorySeparatorChar))))
            .ToList();

        Assert.True(
            missing.Count == 0,
            "These are exempted from the firm-name check and no longer exist: "
            + string.Join(", ", missing));
    }

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
