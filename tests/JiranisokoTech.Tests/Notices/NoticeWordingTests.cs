using JiranisokoTech.Domain.Notices;

namespace JiranisokoTech.Tests.Notices;

/// <summary>
/// Every kind of notice has words written for it, and the fallback tells nobody anything.
/// </summary>
/// <remarks>
/// The sibling of <see cref="Web.SearchHeadingTests"/>, and it exists because the notice page had
/// the same arrangement the search page was caught with, one degree worse.
///
/// Two switches named five of the six kinds and let <c>IncidentRaised</c> fall through to the
/// default, so the default was <em>load-bearing</em>: <c>_ =&gt; "Incident"</c> and
/// <c>_ =&gt; "Something you own is broken"</c>. It read as tidy. What it meant was that the next
/// kind added to the enum would be labelled an incident, in the danger colour, on the one page
/// people open to find out whether something is on fire — and section 26's escalation is exactly
/// such a kind, added while this was being written.
///
/// A fallback that guesses is worse than one that admits it does not know, because the guess is
/// read as a fact about what happened. So the default now says nothing, every kind is named, and
/// this walks the enum.
/// </remarks>
public class NoticeWordingTests
{
    [Fact]
    public void Every_kind_of_notice_has_words_for_it()
    {
        var unnamed = Enum.GetValues<NoticeKind>()
            .Where(kind =>
                JiranisokoTech.Web.Components.Pages.Notices.WordsFor(kind) is
                    { Heading: null } or { Meaning: null })
            .ToList();

        Assert.True(
            unnamed.Count == 0,
            "These kinds of notice have no words written for them, so the page shows a "
            + "placeholder where it should say what happened: " + string.Join(", ", unnamed)
            + "\n\nName them in Said and Means on Notices.razor. Do not lean on the default — "
            + "it used to say \"Incident\", and that is how a new kind gets mislabelled as one.");
    }
}
