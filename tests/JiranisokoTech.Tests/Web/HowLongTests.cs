using JiranisokoTech.Web;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// A length of time, said the way a person would say it.
/// </summary>
/// <remarks>
/// <b>This exists because six copies of one helper all said "1 days".</b>
///
/// The two help desk pages, the two incident pages, the incident review and the service page each
/// carried their own private <c>Length(TimeSpan)</c>, and every one of them wrote
/// <c>$"{(int)span.TotalDays} days"</c>. So any span landing between one and two days rendered as
/// "1 days" — how long an incident ran, how long ago a promise was missed, how long the firm has
/// to answer. Often enough that somebody would have noticed; rare enough that nobody had.
///
/// Found by opening the help desk in a browser and reading the sentence under the priority select:
/// "The firm answers within 1 days and settles it within 5 days."
///
/// The six are one now, in <c>Words</c>, which exists for exactly this reason — its own remark
/// says a switch copied onto each page drifts. A hundred and ninety-two lines of duplication went
/// with them.
/// </remarks>
public class HowLongTests
{
    [Theory]
    [InlineData(0, 0, 30, "under a minute")]
    [InlineData(0, 1, 0, "1 min")]
    [InlineData(0, 2, 0, "2 min")]
    [InlineData(0, 59, 0, "59 min")]
    [InlineData(1, 0, 0, "1 hr")]
    [InlineData(4, 15, 0, "4 hr 15 min")]
    [InlineData(23, 59, 0, "23 hr 59 min")]
    public void A_span_under_a_day_is_said_in_hours_and_minutes(
        int hours, int minutes, int seconds, string said) =>
        Assert.Equal(said, Words.HowLong(new TimeSpan(hours, minutes, seconds)));

    /// <summary>
    /// One day is a day.
    /// </summary>
    /// <remarks>
    /// The case all six copies got wrong, and the reason this file exists. Thirty hours is a day
    /// and a bit, which is what a real promise missed overnight looks like.
    /// </remarks>
    [Theory]
    [InlineData(24, "1 day")]
    [InlineData(30, "1 day")]
    [InlineData(47, "1 day")]
    [InlineData(48, "2 days")]
    [InlineData(24 * 15, "15 days")]
    public void A_span_of_days_says_day_for_one_and_days_for_the_rest(int hours, string said) =>
        Assert.Equal(said, Words.HowLong(TimeSpan.FromHours(hours)));

    /// <summary>
    /// A span that has not begun says so, in whichever words the page needs.
    /// </summary>
    /// <remarks>
    /// Two callers want two sentences for the same state: a duration that has run backwards is
    /// "no time", and a countdown to a deadline that has just passed is "moments". Reachable on
    /// both, because a clock and a stored promise can disagree by a second.
    /// </remarks>
    [Fact]
    public void A_span_that_has_not_begun_says_what_the_page_asked_for()
    {
        Assert.Equal("no time", Words.HowLong(TimeSpan.FromMinutes(-1)));
        Assert.Equal("moments", Words.HowLong(TimeSpan.FromMinutes(-1), "moments"));
        Assert.Equal("under a minute", Words.HowLong(TimeSpan.Zero));
    }
}
