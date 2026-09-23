using JiranisokoTech.Domain.Engineering;

namespace JiranisokoTech.Tests.Engineering;

/// <summary>
/// Finding the work item number in something a developer typed.
/// </summary>
/// <remarks>
/// The hinge the Git integration turns on, and the one piece of it with no
/// safety net. Everything else in this system is given its data by somebody
/// filling a form in; this reads whatever a person happened to write in a
/// branch name while thinking about something else, and decides from that which
/// piece of work a commit belongs to.
///
/// The failure that matters is not missing a reference. It is finding one that
/// is not there — because a commit attached to the wrong task is invisible, and
/// it puts evidence of work on something nobody did. So roughly half of these
/// are about what must NOT match.
/// </remarks>
public class WorkReferenceTests
{
    [Theory]
    [InlineData("feature/412-payment-api", 412)]
    [InlineData("412-payment-api", 412)]
    [InlineData("bugfix/#412", 412)]
    [InlineData("Fixes #412", 412)]
    [InlineData("#412 add the retry", 412)]
    [InlineData("meshack/412_retry", 412)]
    [InlineData("feature/412", 412)]
    public void A_reference_a_developer_would_type_is_found(string text, int expected) =>
        Assert.Equal(expected, WorkReference.FirstIn(text));

    /// <summary>
    /// Numbers that are not work references are left alone.
    /// </summary>
    /// <remarks>
    /// Each of these appears in real branch names and commit messages, and each
    /// would attach work to whatever task happened to hold that number — which
    /// is a wrong answer delivered with confidence, and the reason the pattern
    /// is as narrow as it is.
    /// </remarks>
    [Theory]
    [InlineData("v1.412.0")]
    [InlineData("release/2026-412")]
    [InlineData("fix-412-errors")]
    [InlineData("PR-412")]
    [InlineData("bump dependency to 4.12.0")]
    [InlineData("handle 412 Precondition Failed")]
    [InlineData("main")]
    [InlineData("")]
    public void A_number_that_is_not_a_reference_is_not_found(string text) =>
        Assert.Null(WorkReference.FirstIn(text));

    /// <summary>
    /// A pull request naming two tasks is attached to the first.
    /// </summary>
    /// <remarks>
    /// A guess, and deliberately a visible one. Attaching to both would quietly
    /// double the evidence of work; attaching to neither would lose it. The
    /// first is on a screen somebody can correct.
    /// </remarks>
    [Fact]
    public void Several_references_are_found_in_order()
    {
        var found = WorkReference.In("Fixes #412 and #87");

        Assert.Equal([412, 87], found);
        Assert.Equal(412, WorkReference.FirstIn("Fixes #412 and #87"));
    }

    /// <summary>The same number written twice is one reference.</summary>
    [Fact]
    public void A_repeated_reference_is_counted_once()
    {
        var found = WorkReference.In("feature/412-retry", "#412 add the retry", "Fixes #412");

        Assert.Equal([412], found);
    }

    /// <summary>
    /// Branch and title are read together, because either may carry it.
    /// </summary>
    /// <remarks>
    /// A developer who branches `feature/payment-api` and titles the pull
    /// request "#412 payment API" has said it once, and once is enough.
    /// </remarks>
    [Fact]
    public void The_reference_is_found_wherever_it_was_written()
    {
        Assert.Equal(412, WorkReference.FirstIn("feature/payment-api", "#412 payment API"));
        Assert.Equal(412, WorkReference.FirstIn("feature/412-payment-api", "Payment API"));
        Assert.Null(WorkReference.FirstIn("feature/payment-api", "Payment API"));
    }

    /// <summary>Nothing at all is not a match.</summary>
    [Fact]
    public void Nothing_is_found_in_nothing() =>
        Assert.Null(WorkReference.FirstIn(null, "", "   "));
}
