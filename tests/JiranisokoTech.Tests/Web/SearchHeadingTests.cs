using JiranisokoTech.Infrastructure.Search;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// Every kind a search can return has words written for it.
/// </summary>
/// <remarks>
/// The switch on the search page carries a comment predicting this test. Document was added to
/// <c>ResultKind</c> after the switch was written, searched without being listed there, and
/// rendered a bare "3" as a section heading between "2 clients" and "1 invoice". The comment
/// said the next kind added would do the same thing, and it was right: Article did, while
/// section 25 was being built.
///
/// A prediction in a comment is a prediction. This is the same sentence as a test.
/// </remarks>
public class SearchHeadingTests
{
    [Fact]
    public void Every_kind_a_search_returns_has_words_for_it()
    {
        var unnamed = Enum.GetValues<ResultKind>()
            .Where(kind => JiranisokoTech.Web.Components.Pages.Search.WordsFor(kind) is null)
            .ToList();

        Assert.True(
            unnamed.Count == 0,
            "These search result kinds have no heading written for them, so the page shows a "
            + "bare number where a heading belongs: " + string.Join(", ", unnamed));
    }
}
