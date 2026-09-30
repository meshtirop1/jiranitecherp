using System.Text.RegularExpressions;
using JiranisokoTech.Tests.Infrastructure;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// The stylesheet says something about a narrow screen.
/// </summary>
/// <remarks>
/// <b>It said nothing at all.</b> Section 49 asks for the application to be usable on a telephone
/// and its row recorded that the pages had been checked by hand at 375 pixels — which they had.
/// But <c>app.css</c> contained exactly two <c>@media</c> rules in nineteen hundred lines and both
/// were <c>prefers-color-scheme</c>, so every width rule in the project lived in the layout's
/// scoped stylesheet and had come from the Blazor project template. The only thing that changed
/// between a 4K monitor and a 360 pixel phone was that the navigation collapsed.
///
/// That is the shape of gap this file is for: work done once by hand, correct on the day, and
/// written down nowhere that the next page inherits.
///
/// <b>What this can and cannot prove.</b> It reads the stylesheet, so it proves the rules exist
/// and that the layout breakpoint is not the template's any more. It cannot prove a page looks
/// right at 375 pixels — that needs a browser measuring <c>scrollWidth</c> against
/// <c>clientWidth</c>, which is how both the original fault and this fix were actually found, and
/// which is what section 49's row still says is missing. A structural test is not a substitute for
/// that and its own row says so rather than letting the green tick imply otherwise.
/// </remarks>
public partial class PhoneWidthTests
{
    /// <summary>
    /// The one stylesheet every page loads has width-based rules in it.
    /// </summary>
    /// <remarks>
    /// Asserted as a count rather than by name, because naming the selectors would make this a
    /// second copy of the stylesheet that has to be edited whenever the first one is. What is
    /// being defended is that somebody thought about a narrow screen here at all.
    /// </remarks>
    [Fact]
    public void The_stylesheet_has_rules_for_a_narrow_screen()
    {
        var widths = WidthQueries().Matches(Stylesheet()).Count;

        Assert.True(
            widths > 0,
            "app.css has no width-based @media rule. Every width rule in this project would then "
            + "be in MainLayout.razor.css, where they came from the Blazor template — and the "
            + "only difference between a 4K monitor and a 360 pixel phone would be that the "
            + "navigation collapses.");
    }

    /// <summary>
    /// A thumb gets a bigger target than a cursor.
    /// </summary>
    /// <remarks>
    /// Keyed on the pointer rather than on the width, which is the honest test: a tablet with a
    /// keyboard and a phone are the same width and want different things, and a touchscreen
    /// laptop wants this at any width.
    /// </remarks>
    [Fact]
    public void A_touch_screen_gets_targets_a_thumb_can_hit()
    {
        Assert.Contains("@media (pointer: coarse)", Stylesheet());
    }

    /// <summary>
    /// The layout breakpoint is not the project template's any more.
    /// </summary>
    /// <remarks>
    /// <b>641 pixels is the number this is guarding against, and the reason is measured.</b> At a
    /// 641 pixel viewport the content column is 335 pixels — 641 less the 250 pixel sidebar and
    /// the gutters — while at 375 pixels it is 327. So the breakpoint that was supposed to give a
    /// tablet the desktop layout gave it a column eight pixels wider than a phone's, with a
    /// sidebar taking two fifths of the screen, and the sixty-odd tables with five or more columns
    /// were unreadable from 641 all the way to about 1050.
    ///
    /// The layout files are checked rather than app.css because that is where the breakpoint is,
    /// and putting a second one in app.css would be two files disagreeing about where the sidebar
    /// appears.
    /// </remarks>
    [Fact]
    public void The_layout_breakpoint_is_not_the_templates()
    {
        var layout = Path.Combine(
            Repository.Root, "src", "JiranisokoTech.Web", "Components", "Layout");

        var offenders = Directory
            .EnumerateFiles(layout, "*.razor.css")
            .Where(one => File.ReadAllText(one).Contains("641px", StringComparison.Ordinal))
            .Select(one => Path.GetFileName(one))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "These still switch layout at the Blazor template's 641 pixels: "
            + string.Join(", ", offenders)
            + ". At that width the content column is narrower than it is on a phone, because the "
            + "sidebar has come back and taken 250 of it.");
    }

    private static string Stylesheet() =>
        File.ReadAllText(Path.Combine(
            Repository.Root, "src", "JiranisokoTech.Web", "wwwroot", "app.css"));

    /// <remarks>
    /// Width and the two logical properties that mean the same thing, so that rewriting a rule in
    /// <c>inline-size</c> does not read as removing it.
    /// </remarks>
    [GeneratedRegex(@"@media[^{]*\((?:min|max)-(?:width|inline-size)\s*:")]
    private static partial Regex WidthQueries();
}
