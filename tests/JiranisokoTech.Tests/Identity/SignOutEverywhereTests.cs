using System.Net;
using JiranisokoTech.Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace JiranisokoTech.Tests.Identity;

/// <summary>
/// Ending every session an account has, from one of them.
/// </summary>
/// <remarks>
/// Two <see cref="HttpClient"/> instances are two cookie jars, and two cookie
/// jars are two devices: that is the whole reason these tests are written at
/// this level rather than against the service. Calling UpdateSecurityStampAsync
/// and asserting the column changed would prove nothing anybody cares about.
/// What matters is that a cookie already in somebody else's browser stops being
/// accepted, and only a second cookie jar can say whether it does.
/// </remarks>
public class SignOutEverywhereTests(ImmediateStampValidation factory)
    : IClassFixture<ImmediateStampValidation>
{
    private const string Password = "a-long-enough-password";

    /// <summary>
    /// The test this feature exists for.
    /// </summary>
    /// <remarks>
    /// Somebody sees a sign-in from an address they do not recognise. Before
    /// this, all they could do was change their password, and the other session
    /// carried on working — a cookie is not checked against the password, so
    /// changing it ends nothing on its own timetable.
    /// </remarks>
    [Fact]
    public async Task One_device_ends_the_sessions_of_every_other_device()
    {
        await factory.CreateAccountAsync("everywhere@jiranisokotech.co.ke", Password);

        using var laptop = factory.CreateBrowser();
        using var phone = factory.CreateBrowser();

        await SignInAsync(laptop, "everywhere@jiranisokotech.co.ke");
        await SignInAsync(phone, "everywhere@jiranisokotech.co.ke");

        // Both devices are in, which is the state being undone.
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("/account")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/account")).StatusCode);

        var ended = await SignOutEverywhereAsync(laptop);

        Assert.Equal(HttpStatusCode.Redirect, ended.StatusCode);

        /*
         * The phone was never asked and was never told. Its cookie is still in
         * its jar, still signed, still unexpired — what has gone is the stamp it
         * was signed against, and the next request is where it finds out.
         */
        var after = await phone.GetAsync("/account");

        Assert.Equal(HttpStatusCode.Found, after.StatusCode);
        Assert.Contains("/sign-in", after.Headers.Location!.OriginalString);
    }

    /// <summary>
    /// Including the device that pressed it, which is what the page promises.
    /// </summary>
    /// <remarks>
    /// Identity can carry the current session across a stamp roll by re-issuing
    /// its cookie. If somebody adds that later, this is the test that will say
    /// so, and the page text has to change in the same commit.
    /// </remarks>
    [Fact]
    public async Task The_device_that_pressed_it_is_signed_out_as_well()
    {
        await factory.CreateAccountAsync("thisone@jiranisokotech.co.ke", Password);

        using var browser = factory.CreateBrowser();

        await SignInAsync(browser, "thisone@jiranisokotech.co.ke");

        var ended = await SignOutEverywhereAsync(browser);

        Assert.Equal("/sign-in", Landing(ended).AbsolutePath);

        var after = await browser.GetAsync("/account");

        Assert.Equal(HttpStatusCode.Found, after.StatusCode);

        // And it lands somewhere that says what happened. A button that empties
        // a response and shows a bare login form has, as far as the person can
        // tell, thrown them out for no reason.
        var landing = await browser.GetAsync(Landing(ended).PathAndQuery);

        Assert.Contains("Every session was ended", await landing.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// It is not a password change, and the page says so in as many words.
    /// </summary>
    /// <remarks>
    /// Worth a test rather than a comment, because the two are easy to conflate
    /// and the difference is the whole advice given on the account page: whoever
    /// knows the password can sign straight back in, so ending the sessions is
    /// half of what somebody in trouble needs to do.
    /// </remarks>
    [Fact]
    public async Task It_does_not_change_the_password()
    {
        await factory.CreateAccountAsync("samepass@jiranisokotech.co.ke", Password);

        using var browser = factory.CreateBrowser();

        await SignInAsync(browser, "samepass@jiranisokotech.co.ke");
        await SignOutEverywhereAsync(browser);

        using var again = factory.CreateBrowser();

        await SignInAsync(again, "samepass@jiranisokotech.co.ke");

        Assert.Equal(HttpStatusCode.OK, (await again.GetAsync("/account")).StatusCode);
    }

    /// <summary>
    /// Setting a password already did this, and nothing had to be added for it.
    /// </summary>
    /// <remarks>
    /// <c>UserManager.ResetPasswordAsync</c> rolls the security stamp itself, so
    /// the sessions open at the time die exactly the way "sign out everywhere"
    /// kills them. That is worth pinning down precisely because it is invisible
    /// in our own code: nothing in UserAdministration.SetPasswordAsync mentions
    /// sessions, and somebody who replaced that call with a hand-written update
    /// of the password hash would take the behaviour away without touching a line
    /// that looks like it is about sessions at all.
    /// </remarks>
    [Fact]
    public async Task Setting_a_new_password_ends_the_sessions_that_were_open()
    {
        await factory.CreateAccountAsync("newpass@jiranisokotech.co.ke", Password);

        using var browser = factory.CreateBrowser();

        await SignInAsync(browser, "newpass@jiranisokotech.co.ke");

        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/account")).StatusCode);

        await factory.InScopeAsync(async services =>
        {
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var stored = await users.FindByEmailAsync("newpass@jiranisokotech.co.ke");

            await services.GetRequiredService<UserAdministration>().SetPasswordAsync(
                "newpass@jiranisokotech.co.ke",
                await users.GeneratePasswordResetTokenAsync(stored!),
                "a-different-long-password");
        });

        var after = await browser.GetAsync("/account");

        Assert.Equal(HttpStatusCode.Found, after.StatusCode);
        Assert.Contains("/sign-in", after.Headers.Location!.OriginalString);
    }

    /// <summary>
    /// A post carrying no antiforgery token is not somebody pressing the button.
    /// </summary>
    /// <remarks>
    /// The forgery this prevents is worth naming: another site submits this form
    /// in the background from a browser that is signed in here, and every device
    /// belonging to that person is thrown out. Harmless as damage goes, and
    /// unattributable, which is why it would be reported as the system losing
    /// sessions at random.
    /// </remarks>
    [Fact]
    public async Task A_post_without_a_token_ends_nothing()
    {
        await factory.CreateAccountAsync("forged@jiranisokotech.co.ke", Password);

        using var laptop = factory.CreateBrowser();
        using var phone = factory.CreateBrowser();

        await SignInAsync(laptop, "forged@jiranisokotech.co.ke");
        await SignInAsync(phone, "forged@jiranisokotech.co.ke");

        var response = await laptop.PostAsync("/account", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["_handler"] = "sign-out-everywhere" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Both sessions, including the one the forged post was sent from.
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("/account")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/account")).StatusCode);
    }

    private async Task SignInAsync(HttpClient browser, string email)
    {
        var form = await browser.GetAsync("/sign-in");

        var fields = HtmlForm.Fill(
            await form.Content.ReadAsStringAsync(),
            new Dictionary<string, string>
            {
                ["Input.Email"] = email,
                ["Input.Password"] = Password,
            });

        await browser.PostAsync("/sign-in", new FormUrlEncodedContent(fields));
    }

    /// <summary>
    /// Press the button, the way a browser presses it.
    /// </summary>
    /// <remarks>
    /// The fields come off the rendered page rather than being named here, so
    /// this cannot pass against an account page that has stopped emitting the
    /// antiforgery token or the handler name. That is the failure worth
    /// catching: both are hidden inputs nobody looks at, and a form missing
    /// either one is refused in production while a test that supplies them by
    /// hand goes on passing.
    /// </remarks>
    private static async Task<HttpResponseMessage> SignOutEverywhereAsync(HttpClient browser)
    {
        var page = await browser.GetAsync("/account");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        var fields = HtmlForm.Fill(await page.Content.ReadAsStringAsync());

        Assert.Equal("sign-out-everywhere", fields["_handler"]);

        return await browser.PostAsync("/account", new FormUrlEncodedContent(fields));
    }

    /// <summary>Where a response sends the browser next, as an absolute address.</summary>
    private static Uri Landing(HttpResponseMessage response) =>
        new(new Uri("https://localhost"), response.Headers.Location!);
}

/// <summary>
/// The application with the security stamp checked on every single request.
/// </summary>
/// <remarks>
/// Production checks it once a minute — one read of one row per signed-in person
/// per minute, and the trade is argued where the number is set, in
/// IdentityConfiguration. A minute is also far longer than any test should sit
/// still waiting, so this factory turns the interval down to nothing:
/// TimeSpan.Zero means "check every time", and a revoked cookie is then refused
/// on its very next request instead of within the minute.
///
/// Only the interval moves, and only here. It is a schedule, not a rule: what
/// these tests are about is whether a rolled stamp reaches a cookie held by
/// another browser at all, and shortening the wait does not make that any easier
/// to pass. Shortening it in production so a test need not think about timing
/// would be the other thing entirely — it would put the read on every request of
/// every page to spare one test class a clock.
/// </remarks>
public sealed class ImmediateStampValidation : ApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // ConfigureTestServices and not ConfigureServices: options callbacks run
        // in the order they were registered and the last one wins, so this has
        // to be added after the application's own, which is what this hook
        // guarantees and the other does not.
        builder.ConfigureTestServices(services =>
            services.Configure<SecurityStampValidatorOptions>(
                options => options.ValidationInterval = TimeSpan.Zero));
    }
}
