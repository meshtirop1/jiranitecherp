using System.Net;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Tests.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;
using Repository = JiranisokoTech.Domain.Engineering.Repository;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// The machinery screens, rendered and pressed.
/// </summary>
/// <remarks>
/// Both of the faults section 84 closed were of one kind: something that needed a person,
/// with no way for a person to act. That kind cannot be tested below the page — a service
/// method with no button reachable from anywhere is exactly the state these screens were
/// added to leave behind, so the test has to go through the form.
///
/// The forms are posted rather than merely rendered for the same reason. These pages are
/// statically rendered, which means a button outside a form does nothing at all and looks
/// completely correct in the markup; the only way to know a button works is to press it.
/// </remarks>
public class MachineryPageTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    private const string Password = "a-long-enough-password";

    [Theory]
    [InlineData("/settings/machinery")]
    [InlineData("/settings/events")]
    public async Task A_stranger_is_sent_to_sign_in(string path)
    {
        using var browser = factory.CreateBrowser();

        var response = await browser.GetAsync(path);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("/sign-in", response.Headers.Location!.OriginalString);
    }

    /// <summary>
    /// An engineer with a login is not an administrator.
    /// </summary>
    /// <remarks>
    /// These screens carry a button that replays domain events and a button that runs a job,
    /// so the door matters as much as the page. Deny by default means a missing attribute
    /// shows up as a refusal rather than as an open page, but only if somebody checks.
    /// </remarks>
    [Fact]
    public async Task An_engineer_may_not_look_at_the_machinery()
    {
        var browser = await SignedInAsync("machinery-dev@jiranisokotech.co.ke", Roles.Developer);

        var machinery = await browser.GetAsync("/settings/machinery");
        var events = await browser.GetAsync("/settings/events");

        Assert.NotEqual(HttpStatusCode.OK, machinery.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, events.StatusCode);
    }

    /// <summary>
    /// An abandoned event is listed, and pressing the button actually puts it back.
    /// </summary>
    /// <remarks>
    /// The whole of section 84's first half in one test. Before this the machinery screen
    /// counted these rows and its own alert said they "will not be tried again without
    /// somebody", while offering nobody anything to do — so the assertion that matters is
    /// the state of the row after the post, not the words on the page before it.
    /// </remarks>
    [Fact]
    public async Task An_abandoned_event_can_be_put_back_from_the_screen()
    {
        var browser = await SignedInAsync(
            "machinery-events@jiranisokotech.co.ke", Roles.Administrator);

        var id = await AbandonAnEventAsync("ClientTakenOn");

        var page = await browser.GetAsync("/settings/events");
        var html = await page.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Put it back", html);

        // The class name offered in words as well, for somebody scanning fifty rows.
        Assert.Contains("Client taken on", html);

        var fields = HtmlForm.Fill(
            html, new Dictionary<string, string> { ["_handler"] = $"revive-{id}" });

        var posted = await browser.PostAsync("/settings/events", new FormUrlEncodedContent(fields));

        Assert.NotEqual(HttpStatusCode.BadRequest, posted.StatusCode);

        await factory.InScopeAsync(async services =>
        {
            var database = services.GetRequiredService<AppDbContext>();
            var message = await database.Outbox.SingleAsync(one => one.Id == id);

            Assert.Null(message.AbandonedAt);
            Assert.Equal(0, message.Attempts);
            Assert.True(message.IsPending);
        });
    }

    /// <summary>
    /// A row whose event class is gone says so on the screen.
    /// </summary>
    /// <remarks>
    /// Because the button will not help it. The dispatcher abandons it again within seconds,
    /// permanently, and a screen that offered the same words for both would have somebody
    /// pressing it, watching the row reappear, and pressing it again.
    /// </remarks>
    [Fact]
    public async Task An_event_this_build_no_longer_has_is_flagged_on_the_screen()
    {
        var browser = await SignedInAsync(
            "machinery-gone@jiranisokotech.co.ke", Roles.Administrator);

        await AbandonAnEventAsync("SomethingDeletedInVersionTwo");

        var html = await (await browser.GetAsync("/settings/events")).Content.ReadAsStringAsync();

        /*
         * Matched on a fragment that sits on one line of the component. Razor keeps the
         * source's own line breaks, so asserting a whole sentence of prose asserts the
         * indentation it happens to be written with.
         */
        Assert.Contains("no longer exists in the code", html);
    }

    /// <summary>
    /// A code host that cannot be reached is named, at the top, in red.
    /// </summary>
    /// <remarks>
    /// The fault nothing else in the system could show. A repository is watched, no secret is
    /// configured for its host, and every delivery is refused before a row is written — so
    /// the three queue depths on this same page read zero and every job reads green. The
    /// banner is above them for exactly that reason.
    /// </remarks>
    [Fact]
    public async Task A_host_that_cannot_reach_us_is_named_on_the_machinery_screen()
    {
        var browser = await SignedInAsync(
            "machinery-hosts@jiranisokotech.co.ke", Roles.Administrator);

        await factory.InScopeAsync(async services =>
        {
            var database = services.GetRequiredService<AppDbContext>();

            database.Repositories.Add(Repository.Connect(
                GitProvider.GitLab,
                "jiranisokotech",
                "delivery-app",
                null,
                new string('0', 64),
                DateTimeOffset.UtcNow));

            await database.SaveChangesAsync();
        });

        var html = await (await browser.GetAsync("/settings/machinery"))
            .Content.ReadAsStringAsync();

        Assert.Contains("No secret configured", html);
        Assert.Contains("cannot deliver anything here", html);
    }

    /// <summary>
    /// A job runs from the screen, and running it does not clear its overdue badge.
    /// </summary>
    /// <remarks>
    /// The second assertion is the one worth having. The obvious implementation records an
    /// on-demand run like any other, and then somebody who presses this button to check a job
    /// they suspect has stopped clears the badge by checking — so a dead scheduler reads as
    /// healthy for as long as anybody keeps looking at it.
    ///
    /// Here the scheduler has never run this job, so the row says so in its own words rather
    /// than reporting a job that finished a second ago as merely overdue.
    /// </remarks>
    [Fact]
    public async Task A_job_runs_from_the_screen_and_the_history_says_who_asked()
    {
        var browser = await SignedInAsync(
            "machinery-runs@jiranisokotech.co.ke", Roles.Administrator, "Mesh Tirop");

        var page = await browser.GetAsync("/settings/machinery");
        var html = await page.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Run it now", html);

        var fields = HtmlForm.Fill(
            html, new Dictionary<string, string> { ["_handler"] = "run-jobs.prune" });

        var posted = await browser.PostAsync(
            "/settings/machinery", new FormUrlEncodedContent(fields));

        Assert.NotEqual(HttpStatusCode.BadRequest, posted.StatusCode);

        await factory.InScopeAsync(async services =>
        {
            var database = services.GetRequiredService<AppDbContext>();

            var run = await database.JobRuns.SingleAsync(one => one.Job == "jobs.prune");

            Assert.Equal("Mesh Tirop", run.AskedBy);
            Assert.False(run.WasScheduled);
        });

        var after = await (await browser.GetAsync("/settings/machinery"))
            .Content.ReadAsStringAsync();

        Assert.Contains("Mesh Tirop", after);

        // The guard: a run somebody asked for is not the scheduler keeping up.
        Assert.Contains("Never on its own", after);
    }

    /// <summary>The events row on the machinery screen leads somewhere.</summary>
    /// <remarks>
    /// It was an empty cell from the day that table was written, while the other two queues
    /// each said "Look". The home page has been pointing readers at this page to deal with
    /// abandoned events for just as long.
    /// </remarks>
    [Fact]
    public async Task The_events_queue_has_a_door_on_the_machinery_screen()
    {
        var browser = await SignedInAsync(
            "machinery-door@jiranisokotech.co.ke", Roles.Administrator);

        var html = await (await browser.GetAsync("/settings/machinery"))
            .Content.ReadAsStringAsync();

        Assert.Contains("/settings/events", html);
    }

    /// <summary>Abandon one outbox row, the way a broken handler leaves it.</summary>
    private async Task<Guid> AbandonAnEventAsync(string type)
    {
        var id = Guid.Empty;

        await factory.InScopeAsync(async services =>
        {
            var database = services.GetRequiredService<AppDbContext>();

            var message = new OutboxMessage(type, "{}", DateTimeOffset.UtcNow);

            message.MarkFailed("Broken.", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
            message.Abandon("Broken, and out of attempts.", DateTimeOffset.UtcNow);

            database.Outbox.Add(message);
            await database.SaveChangesAsync();

            id = message.Id;
        });

        return id;
    }

    private async Task<HttpClient> SignedInAsync(
        string email, string role, string? displayName = null)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            if (await users.FindByEmailAsync(email) is null)
            {
                await factory.CreateAccountAsync(email, Password, displayName ?? email);
            }

            var stored = await users.FindByEmailAsync(email);

            if (!await users.IsInRoleAsync(stored!, role))
            {
                await users.AddToRoleAsync(stored!, role);
            }
        }

        var browser = factory.CreateBrowser();

        var form = await browser.GetAsync("/sign-in");
        var fields = HtmlForm.Fill(
            await form.Content.ReadAsStringAsync(),
            new Dictionary<string, string>
            {
                ["Input.Email"] = email,
                ["Input.Password"] = Password,
            });

        await browser.PostAsync("/sign-in", new FormUrlEncodedContent(fields));

        return browser;
    }
}
