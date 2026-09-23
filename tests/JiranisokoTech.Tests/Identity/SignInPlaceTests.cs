using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Tests.Infrastructure;
using JiranisokoTech.Web.Identity;

namespace JiranisokoTech.Tests.Identity;

/// <summary>
/// Where an account has been used, and whether this is somewhere new.
/// </summary>
/// <remarks>
/// The tests that matter most here are the ones asserting the warning does <em>not</em>
/// fire. A security notice that arrives when nothing happened is worse than none: it
/// trains the person to ignore the one that matters, and the first one they ignore is the
/// real one.
/// </remarks>
public class SignInPlaceTests
{
    private const string Chrome =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
        + "Chrome/141.0.0.0 Safari/537.36";

    private const string ChromeUpdated =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
        + "Chrome/142.0.0.0 Safari/537.36";

    private const string Edge =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
        + "Chrome/141.0.0.0 Safari/537.36 Edg/141.0.0.0";

    private const string Phone =
        "Mozilla/5.0 (Linux; Android 15; Pixel 9) AppleWebKit/537.36 (KHTML, like Gecko) "
        + "Chrome/141.0.0.0 Mobile Safari/537.36";

    /// <summary>
    /// Edge is not Chrome, however hard it insists.
    /// </summary>
    /// <remarks>
    /// Every browser's user agent claims to be the ones before it, so the checks have to
    /// rule out the impersonator before the impersonated. Getting the order wrong would
    /// report every Edge user as being on Chrome, which on a security page means somebody
    /// fails to recognise their own sign-in.
    /// </remarks>
    [Theory]
    [InlineData(Chrome, "Chrome on Windows")]
    [InlineData(Edge, "Edge on Windows")]
    [InlineData(Phone, "Chrome on Android")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) Safari/605.1.15",
        "Safari on a Mac")]
    [InlineData(null, "An unidentified browser")]
    [InlineData("", "An unidentified browser")]
    public void A_browser_is_described_as_somebody_would_recognise_it(
        string? agent, string expected) =>
        Assert.Equal(expected, SignInPlaces.Describe(agent));

    /// <summary>
    /// A browser updating itself is not a new browser.
    /// </summary>
    /// <remarks>
    /// The single most important thing in the description. Chrome changes its version
    /// every few weeks, so grouping on the raw string would produce a new row and a new
    /// warning email every time — and forty rows for one laptop answer nothing.
    /// </remarks>
    [Fact]
    public void A_browser_updating_is_the_same_browser() =>
        Assert.Equal(SignInPlaces.Describe(Chrome), SignInPlaces.Describe(ChromeUpdated));

    [Fact]
    public async Task Somewhere_used_before_is_not_new()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var account = Guid.CreateVersion7();

        await GiveSignIns(fixture, account, ("41.90.1.1", Chrome), ("41.90.1.1", Chrome));

        await using var context = fixture.NewContext();

        Assert.False(await new SignInPlaces(context)
            .IsSomewhereNewAsync(account, "41.90.1.1", ChromeUpdated));
    }

    /// <summary>
    /// A different browser at a known address is new, and so is the reverse.
    /// </summary>
    /// <remarks>
    /// Grouped on the pair rather than on either alone. An address alone covers everybody
    /// in the office; a browser alone follows somebody between the office and home. The
    /// pair is roughly "this person on this machine", which is as close as headers get.
    /// </remarks>
    [Fact]
    public async Task A_different_browser_or_a_different_address_is_new()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var account = Guid.CreateVersion7();

        await GiveSignIns(fixture, account, ("41.90.1.1", Chrome), ("41.90.1.1", Chrome));

        await using var context = fixture.NewContext();
        var places = new SignInPlaces(context);

        Assert.True(await places.IsSomewhereNewAsync(account, "41.90.1.1", Edge));
        Assert.True(await places.IsSomewhereNewAsync(account, "197.232.5.5", Chrome));
    }

    /// <summary>
    /// The first sign-in to a new account is not unfamiliar.
    /// </summary>
    /// <remarks>
    /// Everything is unfamiliar then. Warning somebody about the sign-in they are
    /// performing at that moment is how they learn the warning means nothing.
    /// </remarks>
    [Fact]
    public async Task The_first_sign_in_to_an_account_is_not_a_warning()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var account = Guid.CreateVersion7();

        await using var context = fixture.NewContext();

        Assert.False(await new SignInPlaces(context)
            .IsSomewhereNewAsync(account, "41.90.1.1", Chrome));
    }

    /// <summary>
    /// A failed attempt from somewhere does not make it familiar.
    /// </summary>
    /// <remarks>
    /// Otherwise an attacker gets a free pass by guessing wrong first: one failure from
    /// their address, and the successful attempt that follows is from a place the account
    /// has "been used" before. Only successes count.
    /// </remarks>
    [Fact]
    public async Task A_failed_attempt_does_not_make_a_place_familiar()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var account = Guid.CreateVersion7();

        await GiveSignIns(fixture, account, ("41.90.1.1", Chrome), ("41.90.1.1", Chrome));

        await using (var context = fixture.NewContext())
        {
            context.Set<SignInRecord>().Add(SignInRecord.For(
                account, "a@b.co", SignInOutcome.Refused, fixture.Clock.Now,
                "5.5.5.5", Edge));

            await context.SaveChangesAsync();
        }

        await using var after = fixture.NewContext();

        Assert.True(await new SignInPlaces(after)
            .IsSomewhereNewAsync(account, "5.5.5.5", Edge));
    }

    /// <summary>
    /// The list says how many times and when, and marks a place used only once.
    /// </summary>
    /// <remarks>
    /// A place used once and never again is either a borrowed machine or the one an
    /// attacker used, and both are worth a second look.
    /// </remarks>
    [Fact]
    public async Task The_list_counts_visits_and_marks_the_ones_used_once()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var account = Guid.CreateVersion7();

        await GiveSignIns(
            fixture,
            account,
            ("41.90.1.1", Chrome),
            ("41.90.1.1", ChromeUpdated),
            ("197.232.5.5", Phone));

        await using var context = fixture.NewContext();

        var places = await new SignInPlaces(context).ForAsync(account);

        Assert.Equal(2, places.Count);

        var office = places.Single(place => place.Address == "41.90.1.1");

        // Two visits, even though the browser version moved between them.
        Assert.Equal(2, office.Times);
        Assert.False(office.Once);

        Assert.True(places.Single(place => place.Address == "197.232.5.5").Once);
    }

    /// <summary>One account's history is not another's.</summary>
    [Fact]
    public async Task One_accounts_places_are_not_anothers()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var mine = Guid.CreateVersion7();
        var theirs = Guid.CreateVersion7();

        await GiveSignIns(fixture, mine, ("41.90.1.1", Chrome));
        await GiveSignIns(fixture, theirs, ("5.5.5.5", Edge));

        await using var context = fixture.NewContext();

        Assert.Equal("41.90.1.1", Assert.Single(await new SignInPlaces(context)
            .ForAsync(mine)).Address);
    }

    private static async Task GiveSignIns(
        DatabaseFixture fixture,
        Guid account,
        params (string Address, string Agent)[] attempts)
    {
        await using var context = fixture.NewContext();

        foreach (var (address, agent) in attempts)
        {
            context.Set<SignInRecord>().Add(SignInRecord.For(
                account, "a@b.co", SignInOutcome.Succeeded, fixture.Clock.Now,
                address, agent));

            // Moved, so the first and last times on a place are distinguishable.
            fixture.Clock.Advance(TimeSpan.FromHours(1));
        }

        await context.SaveChangesAsync();
    }
}
