using JiranisokoTech.Tests.Infrastructure;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// Something changes on screen between a press and the answer arriving.
/// </summary>
/// <remarks>
/// <b>Nothing did.</b> The application acknowledged no action of any kind: no <c>aria-busy</c>
/// anywhere, no progress indication anywhere, no <c>@keyframes</c> in <c>app.css</c>, one
/// <c>transition</c> declaration, and not one of its two hundred and fifteen submit buttons
/// changed in any way when pressed. Following a link, posting a form and pressing a button on an
/// interactive island all spent the whole round trip showing a screen byte-identical to the one
/// before the click.
///
/// From the user's side a working control and a dead one are then indistinguishable, so the
/// reasonable thing to do is press again — and on several pages the second press writes the
/// record twice. "The frontend is not smooth at all" was the report; this was the cause.
///
/// <b>It is worse under this rendering model than it would be elsewhere.</b> An enhanced
/// navigation is a DOM patch rather than a browser navigation, so there is not even a tab spinner
/// to fall back on; and a page carrying an interactive island arrives finished-looking and stays
/// dead until its circuit connects, which CLAUDE.md records as a trap that nearly had somebody
/// conclude interactivity did not work in this application at all.
///
/// <b>What this can and cannot prove.</b> It reads the files, so it proves the machinery is
/// present and wired. It cannot prove the timing feels right — whether the bar appears too eagerly
/// or a form's label swap is legible before the page replaces it. That needs a browser and a
/// person, and is the same limit <c>PhoneWidthTests</c> states about itself.
/// </remarks>
public class SayingItHeardYouTests
{
    [Fact]
    public void The_stylesheet_has_something_to_show_while_waiting()
    {
        var css = Stylesheet();

        Assert.Contains("[data-navigating]", css);
        Assert.Contains("[data-pending]", css);
        Assert.Contains("@keyframes", css);
    }

    /// <summary>
    /// The press affordance covers both kinds of button.
    /// </summary>
    /// <remarks>
    /// It named <c>button</c> alone while every other rule in the file pairs
    /// <c>button, .button</c> — so an anchor styled as a button, which is what the pagination and
    /// several page actions are, had no response to being pressed at all. Asserted because the
    /// single-selector version looks finished and is the easy thing to write back.
    /// </remarks>
    [Fact]
    public void Pressing_either_kind_of_button_does_something()
    {
        var css = Stylesheet();

        Assert.Contains("button:active:not(:disabled)", css);
        Assert.Contains(".button:active:not([aria-disabled=\"true\"])", css);
    }

    /// <summary>
    /// The script that drives all of it is loaded.
    /// </summary>
    /// <remarks>
    /// Through <c>Assets</c> like its neighbours. A bare <c>src</c> is served unfingerprinted, so
    /// an edit reaches a browser only once the cache lets go of it — which this codebase has
    /// already paid for once with the palette, and which is CLAUDE.md's "the container serves what
    /// was built" trap in a second costume.
    /// </remarks>
    [Fact]
    public void The_pending_script_is_loaded_and_fingerprinted()
    {
        var shell = File.ReadAllText(Path.Combine(
            Repository.Root, "src", "JiranisokoTech.Web", "Components", "App.razor"));

        Assert.Contains("@Assets[\"pending.js\"]", shell);
    }

    /// <summary>
    /// It listens for the events that exist rather than the ones that do not.
    /// </summary>
    /// <remarks>
    /// <c>enhancedload</c> is the one that matters, and it is the rule <c>filters.js</c> states
    /// with the measurement behind it: across a sidebar navigation <c>DOMContentLoaded</c> fires
    /// zero times. A script here that waited for it would work on the first page somebody opened
    /// and silently do nothing on every page they reached by clicking.
    /// </remarks>
    [Fact]
    public void It_listens_for_the_navigation_that_actually_happens()
    {
        var script = File.ReadAllText(Path.Combine(
            Repository.Root, "src", "JiranisokoTech.Web", "wwwroot", "pending.js"));

        Assert.Contains("enhancednavigationstart", script);
        Assert.Contains("enhancedload", script);

        /*
         * And it must not disable the pressed button during the submit event. A disabled control
         * is not successful, so its name and value are left out of what gets posted — which on a
         * page holding several forms is how the server stops being told which button was pressed.
         * The timeout is what puts the disable after serialisation, and it is the one line here
         * whose removal would look like a simplification.
         */
        Assert.Contains("setTimeout", script);
    }

    private static string Stylesheet() =>
        File.ReadAllText(Path.Combine(
            Repository.Root, "src", "JiranisokoTech.Web", "wwwroot", "app.css"));
}
