using System.Net;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Tests.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JiranisokoTech.Tests.Identity;

/// <summary>
/// Getting back into your own account without asking anybody.
/// </summary>
/// <remarks>
/// Section 4 had no forgotten-password page: an administrator issued a link by hand. That is
/// fine on the afternoon the system is installed and useless at seven on a Sunday evening,
/// when the one person who needs help is the one person who cannot get in to ask for it.
///
/// Most of what is asserted here is about what the page does NOT reveal. A form a stranger
/// can post to, which answers differently for an address that has an account, is a way of
/// testing a list of email addresses against this firm's staff one submission at a time — so
/// the interesting tests are the ones proving two different situations produce identical
/// output.
/// </remarks>
public class ForgottenPasswordTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    /// <summary>
    /// A stranger can open the page at all.
    /// </summary>
    /// <remarks>
    /// The whole point. Everything else in this system redirects to sign-in, and a
    /// forgotten-password page behind a sign-in is a locked building with the key inside.
    /// </remarks>
    [Fact]
    public async Task A_stranger_can_open_it()
    {
        using var browser = factory.CreateBrowser();

        var response = await browser.GetAsync("/forgot-password");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Forgotten your password", html);
    }

    /// <summary>The sign-in page says how to reach it.</summary>
    /// <remarks>
    /// A page nothing links to is a page nobody finds, which is the same as not having built
    /// it — this repository has shipped that exact shape before as a permission with no door.
    /// </remarks>
    [Fact]
    public async Task The_sign_in_page_points_at_it()
    {
        using var browser = factory.CreateBrowser();

        var html = await (await browser.GetAsync("/sign-in")).Content.ReadAsStringAsync();

        Assert.Contains("/forgot-password", html);
    }

    /// <summary>
    /// An address with an account and one without are answered identically.
    /// </summary>
    /// <remarks>
    /// The security property this whole flow is shaped around. Any difference at all — a
    /// different sentence, a different status, a slower answer — turns this form into an
    /// oracle for which addresses belong to staff here.
    /// </remarks>
    [Fact]
    public async Task An_account_and_a_stranger_are_answered_the_same_way()
    {
        await factory.CreateAccountAsync(
            "recover@jiranisokotech.co.ke", "a-long-enough-password", "Recover Me");

        var known = await AskAsync("recover@jiranisokotech.co.ke");
        var unknown = await AskAsync("nobody-at-all@example.test");

        Assert.Equal(known.Status, unknown.Status);

        /*
         * The message somebody reads, not the whole response.
         *
         * Two earlier versions of this compared the bodies — first raw, then with the
         * antiforgery token regexed out — and both failed on a generated value that differs
         * per request. That is worse than a weak test: the failure it produces is
         * indistinguishable from the leak it exists to catch, so whoever sees it next will
         * assume the page is wrong and go looking in the wrong place.
         *
         * What the security property actually says is that the page tells a stranger nothing
         * about whether an address has an account. That is a claim about the visible words.
         */
        Assert.Equal(Said(known.Body), Said(unknown.Body));
        Assert.NotEmpty(Said(known.Body));

        // And neither page names the address back, which would be the obvious leak.
        Assert.DoesNotContain("recover@jiranisokotech.co.ke", known.Body);
    }

    /// <summary>
    /// Every ask is recorded, including the ones that sent nothing.
    /// </summary>
    /// <remarks>
    /// The rows that matched no account are the useful ones. Somebody working through a list
    /// of addresses leaves a trail of exactly them, and since the page deliberately shows
    /// nothing that would tell the two apart, this table is the only place the difference
    /// survives.
    /// </remarks>
    [Fact]
    public async Task An_ask_about_nobody_is_still_written_down()
    {
        await AskAsync("ghost-in-the-list@example.test");

        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<
            JiranisokoTech.Infrastructure.Persistence.AppDbContext>();

        var ask = await database.Set<RecoveryAsk>()
            .AsNoTracking()
            .FirstOrDefaultAsync(one => one.Email == RecoveryAsk.Normalised(
                "ghost-in-the-list@example.test"));

        Assert.NotNull(ask);
        Assert.Equal(RecoveryOutcome.NoSuchAccount, ask.Outcome);
        Assert.Null(ask.UserId);
    }

    /// <summary>
    /// A fourth ask about one address in an hour sends nothing.
    /// </summary>
    /// <remarks>
    /// Counted against the address the mail would go to, not the address the request came
    /// from. The web layer's rate limiter does the latter, which catches a flood from one
    /// machine and does nothing about somebody moving between addresses to bury one person's
    /// inbox in letters from this firm's domain.
    ///
    /// Driven through the service rather than through the form, and that is not a shortcut.
    /// Four posts from one client would run into the five-per-fifteen-minutes IP limiter that
    /// this same change added — together with the other posts in this class, which share the
    /// one test server and therefore one bucket — so an HTTP version of this test would be
    /// measuring the wrong limit and would fail as the class grew. The durable throttle is
    /// service logic and this is where it can be asserted without a second limit in the way.
    /// </remarks>
    [Fact]
    public async Task A_fourth_ask_in_an_hour_sends_nothing()
    {
        const string address = "throttled@jiranisokotech.co.ke";

        await factory.CreateAccountAsync(address, "a-long-enough-password", "Throttle Me");

        for (var asked = 0; asked < UserAdministration.MostLinksAnHour + 1; asked++)
        {
            using var scope = factory.Services.CreateScope();

            await scope.ServiceProvider
                .GetRequiredService<UserAdministration>()
                .AskForARecoveryLinkAsync(address, "https://erp.jiranisokotech.co.ke");
        }

        using var reading = factory.Services.CreateScope();
        var database = reading.ServiceProvider.GetRequiredService<
            JiranisokoTech.Infrastructure.Persistence.AppDbContext>();

        var asks = await database.Set<RecoveryAsk>()
            .AsNoTracking()
            .Where(one => one.Email == RecoveryAsk.Normalised(address))
            .ToListAsync();

        Assert.Equal(
            UserAdministration.MostLinksAnHour,
            asks.Count(one => one.Outcome == RecoveryOutcome.LinkSent));

        // The refused one is recorded too, so a run of them is visible afterwards.
        Assert.Contains(asks, one => one.Outcome == RecoveryOutcome.Throttled);
    }

    /// <summary>
    /// Varying the case does not open a fresh bucket.
    /// </summary>
    /// <remarks>
    /// The fault this test exists for shipped in the first version of this feature and was
    /// caught reading it back. Identity resolves an account through its normalised address, so
    /// mesh@x and MESH@x are one account — but the throttle stored what was typed, so they
    /// were two buckets. Anybody who noticed could fill one inbox with password letters from
    /// this firm's own domain by changing a letter's case, while the limit reported nothing
    /// wrong.
    /// </remarks>
    [Fact]
    public async Task Varying_the_case_does_not_open_a_fresh_bucket()
    {
        const string address = "mixedcase@jiranisokotech.co.ke";

        await factory.CreateAccountAsync(address, "a-long-enough-password", "Mixed Case");

        foreach (var spelling in new[]
        {
            address,
            address.ToUpperInvariant(),
            "MixedCase@Jiranisokotech.co.ke",
            address,
        })
        {
            using var scope = factory.Services.CreateScope();

            await scope.ServiceProvider
                .GetRequiredService<UserAdministration>()
                .AskForARecoveryLinkAsync(spelling, "https://erp.jiranisokotech.co.ke");
        }

        using var reading = factory.Services.CreateScope();
        var database = reading.ServiceProvider.GetRequiredService<
            JiranisokoTech.Infrastructure.Persistence.AppDbContext>();

        var asks = await database.Set<RecoveryAsk>()
            .AsNoTracking()
            .Where(one => one.Email == RecoveryAsk.Normalised(address))
            .ToListAsync();

        // Four spellings, one bucket: three links and then a refusal.
        Assert.Equal(4, asks.Count);
        Assert.Equal(
            UserAdministration.MostLinksAnHour,
            asks.Count(one => one.Outcome == RecoveryOutcome.LinkSent));
        Assert.Contains(asks, one => one.Outcome == RecoveryOutcome.Throttled);
    }

    /// <summary>What the page says happened, as a person reads it.</summary>
    private static string Said(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html, @"class=""signin__said""[^>]*>(?<said>.*?)</p>",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        return match.Success
            ? System.Text.RegularExpressions.Regex.Replace(
                match.Groups["said"].Value, @"\s+", " ").Trim()
            : string.Empty;
    }

    private async Task<(HttpStatusCode Status, string Body)> AskAsync(string email)
    {
        using var browser = factory.CreateBrowser();

        var form = await browser.GetAsync("/forgot-password");

        var fields = HtmlForm.Fill(
            await form.Content.ReadAsStringAsync(),
            new Dictionary<string, string> { ["Input.Email"] = email });

        var response = await browser.PostAsync(
            "/forgot-password", new FormUrlEncodedContent(fields));

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}
