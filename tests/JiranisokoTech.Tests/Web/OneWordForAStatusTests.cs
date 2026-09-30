using System.Text.RegularExpressions;
using JiranisokoTech.Tests.Infrastructure;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// A work item's status is worded in one place.
/// </summary>
/// <remarks>
/// <b>Five pages each carried a copy of the same switch.</b> Three wrote it in title case —
/// <c>Home.razor</c>, <c>Board.razor</c>, <c>Item.razor</c> — and two in lower case, on
/// <c>Planning.razor</c> and <c>WorkParts.razor</c>. They had not disagreed about a word yet, and
/// that is the whole reason this is worth a test rather than a tidy-up: the six copies of
/// <c>Length(TimeSpan)</c> had not disagreed either, until one of them started writing "1 days"
/// on the help desk, the incidents list and everywhere else a span landed between one and two
/// days. CLAUDE.md records that as a trap and states the rule — before writing a small formatting
/// helper on a page, look in <c>Words.cs</c>; if it belongs there, it goes there.
///
/// The status is the most-rendered value in the work half of this application: it is a pill on the
/// board, on the home page, on a work item, on its children, on its blockers and in a planning
/// table. Five copies is five places for one renaming to be applied four times.
///
/// Worth recording that a scouting pass over this found four of the five and missed the two
/// lower-case ones, because it searched for the title-case string. The regex below matches the
/// enum member and the arrow and not the word on the right of it, so a sixth copy in any casing
/// is caught.
///
/// <see cref="JiranisokoTech.Domain.Work.WorkItem"/> keeps its own private copy and is exempt by
/// living outside the web project. It words exception text, the domain cannot reference the web
/// project, and a sentence thrown at a caller is not a sentence on a screen.
/// </remarks>
public partial class OneWordForAStatusTests
{
    [Fact]
    public void No_page_words_a_work_item_status_for_itself()
    {
        var web = Path.Combine(Repository.Root, "src", "JiranisokoTech.Web");

        var offenders = new List<string>();

        foreach (var path in Directory
            .EnumerateFiles(web, "*.razor", SearchOption.AllDirectories)
            .Where(one => !one.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !one.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(path);
            var relative = Path.GetRelativePath(web, path).Replace('\\', '/');

            foreach (Match found in Copied().Matches(text))
            {
                var line = text.Take(found.Index).Count(one => one == '\n') + 1;

                offenders.Add($"  {relative}:{line}  {found.Value.Trim()}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These word a work item's status on the page instead of asking Words for it:\n"
            + string.Join('\n', offenders.Order())
            + "\n\nCall Words.For(WorkItemStatus), with .ToLowerInvariant() where the word sits "
            + "inside a sentence. Five pages had a copy of that switch and a sixth is how one "
            + "renaming gets applied five times.");
    }

    /// <summary>
    /// <c>Words</c> still has the method the failure above tells everybody to call.
    /// </summary>
    /// <remarks>
    /// The first version of this remark claimed it guarded against the method being deleted, and
    /// that was wrong: eight call sites reference it, so removing or renaming it stops the build
    /// before any test runs. Proved by renaming it and watching the compiler, not the suite,
    /// object.
    ///
    /// What it does catch is the one shape neither the compiler nor the test above can see. Leave
    /// the signature and every call site in place, delete the three named arms, and
    /// <c>Words.For</c> becomes a passthrough to <c>ToString</c> -- the build is clean, no page
    /// has a switch of its own, and every screen in the work half of the application quietly reads
    /// "Todo" and "InProgress". Proved by hollowing the method out and watching this fail.
    /// </remarks>
    [Fact]
    public void Words_has_the_one_it_tells_everybody_to_call()
    {
        var words = File.ReadAllText(
            Path.Combine(Repository.Root, "src", "JiranisokoTech.Web", "Words.cs"));

        Assert.Contains("public static string For(WorkItemStatus status)", words);
        Assert.Contains("WorkItemStatus.Todo => \"To do\"", words);
    }

    /// <remarks>
    /// Matched on the enum member and the arrow rather than on the word, so a copy worded in lower
    /// case is caught as readily as one in title case — which is the half a scouting search for
    /// the literal "To do" missed twice. <c>Words.cs</c> is not a <c>.razor</c> file, so it needs
    /// no exemption; the one legitimate copy is there.
    /// </remarks>
    [GeneratedRegex(@"WorkItemStatus\.(Todo|InProgress|InReview)\s*(or\s+WorkItemStatus\.\w+\s*)*=>\s*""")]
    private static partial Regex Copied();
}
