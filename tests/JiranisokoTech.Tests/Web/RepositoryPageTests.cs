using System.Net;
using JiranisokoTech.Application.Engineering;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Tests.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// The repository screens, rendered.
/// </summary>
/// <remarks>
/// These exist because of what this codebase has already learnt the hard way.
/// Four faults reached it that every unit test passed over — an unstyled sign-in
/// page, a rate limit on the wrong verb, a mobile layout overflowing, and a
/// content security policy that blocked the framework's own script — and each was
/// found by opening a page and looking at it. Rendering through the real pipeline
/// is the nearest a test gets to looking.
/// </remarks>
public class RepositoryPageTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    private const string Password = "a-long-enough-password";

    [Theory]
    [InlineData("/repositories")]
    [InlineData("/repositories/deliveries")]
    public async Task A_stranger_is_sent_to_sign_in(string path)
    {
        using var browser = factory.CreateBrowser();

        var response = await browser.GetAsync(path);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("/sign-in", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task The_repositories_screen_renders_for_somebody_who_may_see_it()
    {
        var browser = await SignedInAsync("repos-view@jiranisokotech.co.ke", Roles.Administrator);

        var response = await browser.GetAsync("/repositories");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Connect a repository", html);

        // The address to paste into GitHub. Getting this wrong means somebody
        // configures a webhook that posts into nothing.
        Assert.Contains("/webhooks/github", html);
    }

    /// <summary>
    /// An engineer may look, and may not connect.
    /// </summary>
    /// <remarks>
    /// The split that repos.manage exists for. Connecting a repository means
    /// holding the secret that signs its deliveries, and pointing one at the
    /// wrong project files everybody's work under the wrong name.
    /// </remarks>
    [Fact]
    public async Task An_engineer_may_look_but_not_connect()
    {
        var browser = await SignedInAsync("repos-dev@jiranisokotech.co.ke", Roles.Developer);

        var response = await browser.GetAsync("/repositories");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("Connect a repository", html);
    }

    /// <summary>
    /// And may not read the delivery log.
    /// </summary>
    /// <remarks>
    /// The narrowest of the three permissions, because a delivery body carries
    /// branch names, commit messages and the shape of a private codebase's
    /// history. Seeing that a repository exists is not the same as being able to
    /// read everything that has ever happened inside it.
    /// </remarks>
    [Fact]
    public async Task An_engineer_cannot_read_the_delivery_log()
    {
        var browser = await SignedInAsync("repos-log@jiranisokotech.co.ke", Roles.Developer);

        var response = await browser.GetAsync("/repositories/deliveries");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// A connected repository shows on the screen, and shows that it has been
    /// heard from.
    /// </summary>
    [Fact]
    public async Task A_connected_repository_is_shown_with_what_is_known_about_it()
    {
        await factory.InScopeAsync(async services =>
        {
            var database = services.GetRequiredService<AppDbContext>();

            database.Repositories.Add(Repository.Connect(
                GitProvider.GitHub,
                "jiranisokotech",
                "delivery-notes",
                null,
                new string('0', 64),
                DateTimeOffset.UtcNow));

            await database.SaveChangesAsync();
        });

        var browser = await SignedInAsync("repos-list@jiranisokotech.co.ke", Roles.Administrator);

        var html = await (await browser.GetAsync("/repositories")).Content.ReadAsStringAsync();

        Assert.Contains("jiranisokotech/delivery-notes", html);

        // Nothing has ever arrived, and the screen says so rather than leaving a
        // blank cell that reads as "fine".
        Assert.Contains("Nothing yet", html);

        /*
         * The stored hash is deliberately not the fingerprint of any configured
         * secret, so the tripwire should be showing. A row that looked healthy
         * here would be the exact failure this column exists to catch: a
         * repository that is connected, looks connected, and is refusing every
         * delivery that arrives.
         */
        Assert.Contains("Secret differs", html);
    }

    /// <summary>
    /// The work item page shows what the repositories say was built against it.
    /// </summary>
    /// <remarks>
    /// The payoff for the whole integration. Nobody typed any of this: a branch
    /// was named after the work, and the commit arrived attached to it.
    /// </remarks>
    [Fact]
    public async Task The_work_item_page_shows_the_commits_made_against_it()
    {
        Guid workItemId = Guid.Empty;

        await factory.InScopeAsync(async services =>
        {
            var database = services.GetRequiredService<AppDbContext>();

            var repository = Repository.Connect(
                GitProvider.GitHub, "jiranisokotech", "erp-evidence", null,
                new string('0', 64), DateTimeOffset.UtcNow);

            var last = await database.WorkItems.AnyAsync()
                ? await database.WorkItems.MaxAsync(item => item.Number)
                : 0;

            var item = WorkItem.Raise(last + 1, "Print the delivery note", Guid.CreateVersion7());

            database.Repositories.Add(repository);
            database.WorkItems.Add(item);
            await database.SaveChangesAsync();

            database.Commits.Add(Commit.Record(
                repository.Id,
                "9a1c0ff4e6b3d2a18f7c5e0b4d3a2916f8e7c0d5",
                "Lay out the note",
                "meshtirop1",
                $"feature/{item.Number}-delivery-note",
                item.Id,
                DateTimeOffset.UtcNow));

            database.PullRequests.Add(PullRequest.Opened(
                repository.Id, 7, "Delivery note layout",
                $"feature/{item.Number}-delivery-note", "meshtirop1", item.Id,
                DateTimeOffset.UtcNow));

            await database.SaveChangesAsync();

            workItemId = item.Id;
        });

        var browser = await SignedInAsync("repos-item@jiranisokotech.co.ke", Roles.Administrator);

        var html = await (await browser.GetAsync($"/work/{workItemId}")).Content
            .ReadAsStringAsync();

        Assert.Contains("What was built", html);
        Assert.Contains("9a1c0ff", html);
        Assert.Contains("Lay out the note", html);
        Assert.Contains("Delivery note layout", html);

        // Open, with nobody having reviewed it. Both halves are on the screen,
        // because "open" and "approved" are the two things a head of department
        // is deciding on when they release the work.
        Assert.Contains("None yet", html);
    }

    /// <summary>
    /// The work item page says how to link a branch when nothing has arrived.
    /// </summary>
    /// <remarks>
    /// An empty panel saying "no commits" would leave somebody guessing at the
    /// convention. The number is the one thing they have to carry into a branch
    /// name, so the empty state is where it gets spelt out.
    /// </remarks>
    [Fact]
    public async Task The_work_item_page_explains_how_to_link_a_branch()
    {
        Guid workItemId = Guid.Empty;
        var number = 0;

        await factory.InScopeAsync(async services =>
        {
            var database = services.GetRequiredService<AppDbContext>();

            var last = await database.WorkItems.AnyAsync()
                ? await database.WorkItems.MaxAsync(item => item.Number)
                : 0;

            var item = WorkItem.Raise(last + 1, "Nothing built yet", Guid.CreateVersion7());

            database.WorkItems.Add(item);
            await database.SaveChangesAsync();

            workItemId = item.Id;
            number = item.Number;
        });

        var browser = await SignedInAsync("repos-empty@jiranisokotech.co.ke", Roles.Administrator);

        var html = await (await browser.GetAsync($"/work/{workItemId}")).Content
            .ReadAsStringAsync();

        Assert.Contains($"feature/{number}-something", html);
        Assert.Contains($"#{number}", html);
    }

    /// <summary>
    /// The deliveries screen renders, and an empty view says so.
    /// </summary>
    /// <remarks>
    /// Asked for a state nothing in this class produces, rather than for the
    /// whole log. Every test here shares one database, so "the log is empty" is
    /// true only for whichever test runs first — which is how this one first
    /// failed while the screen was correct.
    /// </remarks>
    [Fact]
    public async Task The_deliveries_screen_renders_and_says_when_a_view_is_empty()
    {
        var browser = await SignedInAsync("repos-empty-log@jiranisokotech.co.ke", Roles.Owner);

        var response = await browser.GetAsync(
            $"/repositories/deliveries?status={DeliveryStatus.Handled}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("No deliveries in that state", html);

        // The filters are links, so "show me what failed" is something somebody
        // can send to a colleague.
        Assert.Contains("status=DeadLettered", html);
    }

    /// <summary>
    /// A dead-lettered delivery is shown as needing somebody, with a way to
    /// retry it.
    /// </summary>
    /// <remarks>
    /// The one state on this screen that is a call to action rather than
    /// information. A delivery that has given up means what the board says was
    /// built is incomplete, and saying that plainly is the whole point of the
    /// row.
    /// </remarks>
    [Fact]
    public async Task A_delivery_that_gave_up_is_shown_with_a_way_to_replay_it()
    {
        await factory.InScopeAsync(async services =>
        {
            var database = services.GetRequiredService<AppDbContext>();

            var delivery = WebhookDelivery.Receive(
                GitProvider.GitHub,
                $"dead-{Guid.CreateVersion7()}",
                "pull_request",
                """{"action":"closed"}""",
                null,
                DateTimeOffset.UtcNow);

            for (var attempt = 0; attempt < WebhookDelivery.MaximumAttempts; attempt++)
            {
                delivery.Failed("The payload had no numeric 'number'.", DateTimeOffset.UtcNow);
            }

            database.Deliveries.Add(delivery);
            await database.SaveChangesAsync();
        });

        var browser = await SignedInAsync("repos-dead@jiranisokotech.co.ke", Roles.Owner);

        var html = await (await browser.GetAsync("/repositories/deliveries")).Content
            .ReadAsStringAsync();

        Assert.Contains("Gave up", html);
        Assert.Contains("Try it again", html);
        Assert.Contains("had no numeric", html);

        /*
         * Said at the top as well, because somebody arriving at this screen for
         * another reason still needs to know the record is incomplete.
         *
         * Matched on a fragment that sits on one line of the component. Razor
         * keeps the source's own line breaks, so asserting on a whole sentence
         * of prose asserts the indentation it happens to be written with — which
         * is how this test first failed while the screen was correct.
         */
        Assert.Contains("given up after", html);
    }

    private async Task<HttpClient> SignedInAsync(string email, string role)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            if (await users.FindByEmailAsync(email) is null)
            {
                await factory.CreateAccountAsync(email, Password, email);
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
