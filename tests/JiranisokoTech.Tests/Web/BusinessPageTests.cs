using System.Net;
using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Application.Business;
using JiranisokoTech.Application.Work;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Tests.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// The six business screens, and the question these tests exist to answer: can
/// the people who need a page actually open it?
/// </summary>
/// <remarks>
/// Twice now this system has shipped a permission somebody held with no way to
/// reach the page it applied to — the work board an engineer could not open, the
/// scorecard an interviewer could not see the interview for. Both built, both
/// passed their unit tests, both useless. A page reached over HTTP as a
/// particular role is the only test that catches it.
/// </remarks>
public class BusinessPageTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    private const string Password = "a-long-enough-password";

    [Theory]
    [InlineData("/time")]
    [InlineData("/time/approvals")]
    [InlineData("/leave")]
    [InlineData("/leave/decisions")]
    [InlineData("/expenses")]
    [InlineData("/expenses/claims")]
    [InlineData("/clients")]
    [InlineData("/invoices")]
    public async Task A_stranger_is_sent_to_sign_in(string path)
    {
        using var browser = factory.CreateBrowser();

        var response = await browser.GetAsync(path);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("/sign-in", response.Headers.Location!.OriginalString);
    }

    /// <summary>
    /// A developer — the role holding least — can open all three self-service
    /// pages.
    /// </summary>
    /// <remarks>
    /// The most important test in this file. Logging hours, asking for leave and
    /// claiming expenses are what the majority of the staff use this system for,
    /// and a developer is the role most likely to have been forgotten when a
    /// permission was added to the wrong list.
    /// </remarks>
    [Theory]
    [InlineData("/time", "My time")]
    [InlineData("/leave", "My leave")]
    [InlineData("/expenses", "My expenses")]
    public async Task A_developer_can_open_their_own_pages(string path, string heading)
    {
        var browser = await SignedInAsync("dev@jiranisokotech.co.ke", Roles.Developer);

        var response = await browser.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(heading, html);
    }

    /// <summary>
    /// And is refused the pages about everybody else.
    /// </summary>
    [Theory]
    [InlineData("/time/approvals")]
    [InlineData("/leave/decisions")]
    [InlineData("/expenses/claims")]
    [InlineData("/clients")]
    [InlineData("/invoices")]
    public async Task A_developer_cannot_open_the_pages_about_everybody_else(string path)
    {
        var browser = await SignedInAsync("dev2@jiranisokotech.co.ke", Roles.Developer);

        var response = await browser.GetAsync(path);

        // Signed in but not permitted: sent to the refusal page rather than back
        // to sign-in, because signing in again would not help.
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("/denied", response.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("/time/approvals", "Timesheets")]
    [InlineData("/leave/decisions", "Leave")]
    [InlineData("/expenses/claims", "Expense claims")]
    public async Task A_department_head_can_open_what_they_sign_off(string path, string heading)
    {
        var browser = await SignedInAsync("head@jiranisokotech.co.ke", Roles.DepartmentHead);

        var response = await browser.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(heading, html);
    }

    [Theory]
    [InlineData("/clients", "Clients")]
    [InlineData("/invoices", "Invoices")]
    public async Task A_delivery_manager_can_open_the_money_pages(string path, string heading)
    {
        var browser = await SignedInAsync("pm@jiranisokotech.co.ke", Roles.ProjectManager);

        var response = await browser.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(heading, html);
    }

    /// <summary>
    /// A head can see the debt and not settle it.
    /// </summary>
    /// <remarks>
    /// Approving a claim and paying it are separate permissions, and this checks
    /// that the separation reaches the screen rather than stopping at the role
    /// matrix. The page says who does pay them, because a control that is simply
    /// absent reads as a system that cannot do the thing.
    /// </remarks>
    [Fact]
    public async Task A_head_is_not_offered_the_button_that_pays_a_claim()
    {
        var browser = await SignedInAsync("head2@jiranisokotech.co.ke", Roles.DepartmentHead);

        var response = await browser.GetAsync("/expenses/claims");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("Mark paid", html);
    }

    /// <summary>
    /// Every link in the navigation opens for the role it is shown to.
    /// </summary>
    /// <remarks>
    /// The navigation puts each link behind the permission its page checks, which
    /// makes the two claims easy to keep in step and easy to get wrong in the
    /// same edit. This walks what a developer is actually shown and opens each
    /// one.
    /// </remarks>
    [Fact]
    public async Task Every_link_a_developer_is_shown_opens()
    {
        var browser = await SignedInAsync("dev3@jiranisokotech.co.ke", Roles.Developer);

        var response = await browser.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Attribute order and the "active" class are the renderer's business, so
        // this matches a class containing nav-link rather than equalling it, and
        // takes the href from whichever side of it the attribute lands.
        var found = System.Text.RegularExpressions.Regex
            .Matches(
                html,
                """<a[^>]*?(?:class="[^"]*nav-link[^"]*"[^>]*?href="([^"]*)"|href="([^"]*)"[^>]*?class="[^"]*nav-link[^"]*")""")
            .Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value)
            .ToList();

        // Home is href="" and is not a separate page to open.
        var links = found.Where(href => href.Length > 0).Distinct().ToList();

        Assert.True(
            links.Count > 0,
            $"The navigation rendered no links at all. Anchors found: [{string.Join("|", found)}]");

        foreach (var link in links)
        {
            var page = await browser.GetAsync("/" + link.TrimStart('/'));

            Assert.True(
                page.StatusCode == HttpStatusCode.OK,
                $"The navigation offered /{link} but opening it gave {(int)page.StatusCode}.");
        }
    }

    /// <summary>
    /// A developer cannot open the reporting page, and a manager can.
    /// </summary>
    /// <remarks>
    /// Worth its own test because the page shows money, and money is behind a
    /// second permission inside a page the reporting permission opens. Both
    /// halves are checked below.
    /// </remarks>
    [Fact]
    public async Task Reporting_is_not_for_everybody()
    {
        var developer = await SignedInAsync("dev4@jiranisokotech.co.ke", Roles.Developer);

        var refused = await developer.GetAsync("/reports");

        Assert.Equal(HttpStatusCode.Found, refused.StatusCode);
        Assert.Contains("/denied", refused.Headers.Location!.OriginalString);

        var manager = await SignedInAsync("pm2@jiranisokotech.co.ke", Roles.ProjectManager);

        var allowed = await manager.GetAsync("/reports");
        var html = await allowed.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Contains("Where things stand", html);
    }

    /// <summary>
    /// A head opens the reporting page and is not shown what clients owe.
    /// </summary>
    /// <remarks>
    /// They hold reports, so they see their team's hours and absence. They do
    /// not hold invoices, and the firm's debtors are not theirs to read — a
    /// section rendered for them anyway would be a leak nobody had decided on.
    /// </remarks>
    [Fact]
    public async Task A_head_sees_the_reporting_page_without_the_money_on_it()
    {
        var browser = await SignedInAsync("head3@jiranisokotech.co.ke", Roles.DepartmentHead);

        var response = await browser.GetAsync("/reports");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Where things stand", html);
        Assert.DoesNotContain("Money owed to us", html);
        Assert.DoesNotContain("Work done and not billed", html);
    }

    /// <summary>
    /// Settings is the owner's page and nobody else's.
    /// </summary>
    /// <remarks>
    /// A delivery manager holds a great deal — clients, invoices, the board —
    /// and still does not get to change the firm's PIN or what its invoices are
    /// numbered with. Checked with the most-privileged role short of the top,
    /// because that is the one a permission is most likely to have leaked into.
    /// </remarks>
    [Fact]
    public async Task Settings_is_not_open_to_a_delivery_manager()
    {
        var manager = await SignedInAsync("pm3@jiranisokotech.co.ke", Roles.ProjectManager);

        var refused = await manager.GetAsync("/settings");

        Assert.Equal(HttpStatusCode.Found, refused.StatusCode);
        Assert.Contains("/denied", refused.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task An_administrator_can_open_settings()
    {
        var admin = await SignedInAsync("admin2@jiranisokotech.co.ke", Roles.Administrator);

        var response = await admin.GetAsync("/settings");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Who the firm is", html);
        Assert.Contains("Invoice prefix", html);
    }

    /// <summary>
    /// Anybody signed in can search; what they find is another matter.
    /// </summary>
    /// <remarks>
    /// The page carries no permission beyond being signed in, which is
    /// deliberate and is the kind of decision worth pinning down: a permission
    /// here would either hide the box from most of the staff or promise them
    /// results no page would open. The query decides per group, and this checks
    /// the page itself opens for the role holding least.
    /// </remarks>
    [Fact]
    public async Task A_developer_can_open_the_search_page()
    {
        var browser = await SignedInAsync("dev5@jiranisokotech.co.ke", Roles.Developer);

        var response = await browser.GetAsync("/search?q=acme");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Search", html);
    }

    [Fact]
    public async Task A_stranger_cannot_search()
    {
        using var browser = factory.CreateBrowser();

        var response = await browser.GetAsync("/search?q=acme");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("/sign-in", response.Headers.Location!.OriginalString);
    }

    /// <summary>
    /// The screens that were missing entirely open for the roles granted them.
    /// </summary>
    /// <remarks>
    /// Applications and interviews had a domain, services and tests, and no
    /// page — so the careers site accepted applications nobody could read, and
    /// three roles held scorecards.submit with nothing to submit to.
    /// </remarks>
    [Theory]
    [InlineData("/hiring/applications", "Applications")]
    [InlineData("/hiring/interviews", "Interviews")]
    public async Task Hr_can_open_the_recruitment_screens(string path, string heading)
    {
        var browser = await SignedInAsync("hr2@jiranisokotech.co.ke", Roles.HumanResources);

        var response = await browser.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(heading, html);
    }

    /// <summary>
    /// An interviewer can open the interview they are asked to score.
    /// </summary>
    /// <remarks>
    /// Interviewer is the narrowest role and holds interviews.view precisely so
    /// that this works. It is the role most likely to be forgotten, being the
    /// one nobody holds on its own.
    /// </remarks>
    [Fact]
    public async Task An_interviewer_can_open_interviews()
    {
        var browser = await SignedInAsync("panel@jiranisokotech.co.ke", Roles.Interviewer);

        var response = await browser.GetAsync("/hiring/interviews");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_developer_cannot_open_the_recruitment_screens()
    {
        var browser = await SignedInAsync("dev6@jiranisokotech.co.ke", Roles.Developer);

        foreach (var path in new[] { "/hiring/applications", "/hiring/interviews", "/accounts/roles" })
        {
            var response = await browser.GetAsync(path);

            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            Assert.Contains("/denied", response.Headers.Location!.OriginalString);
        }
    }

    /// <summary>
    /// HR can see timesheets, which they could not before.
    /// </summary>
    /// <remarks>
    /// They hold time.view_all and not time.approve. The page asked for
    /// time.approve, so a permission granted specifically to let HR reconcile
    /// absence against hours let them see nothing at all.
    /// </remarks>
    [Fact]
    public async Task Hr_can_see_timesheets_without_being_able_to_approve_them()
    {
        var browser = await SignedInAsync("hr3@jiranisokotech.co.ke", Roles.HumanResources);

        var response = await browser.GetAsync("/time/approvals");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Timesheets", html);

        // Seen, not signed off. The approve button belongs to time.approve.
        Assert.DoesNotContain("Approve</button>", html);
    }

    /// <summary>
    /// A client can be opened, which is where its documents live.
    /// </summary>
    /// <remarks>
    /// Clients were a flat list with no detail page, so the attachment feature
    /// built for them had nowhere to be reached from — a capability with no
    /// door, which is this codebase's recurring fault in another costume.
    /// </remarks>
    [Fact]
    public async Task A_client_can_be_opened_from_the_list()
    {
        var browser = await SignedInAsync("clientpm@jiranisokotech.co.ke", Roles.ProjectManager);

        using (var scope = factory.Services.CreateScope())
        {
            var clients = scope.ServiceProvider.GetRequiredService<ClientService>();
            await clients.TakeOnAsync($"Openable {Guid.CreateVersion7():N}");
        }

        var list = await browser.GetAsync("/clients");
        var html = await list.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        var link = System.Text.RegularExpressions.Regex.Match(html, @"href=""/clients/([0-9a-f-]{36})""");

        Assert.True(link.Success, "The client list offered no link to open a client.");

        var detail = await browser.GetAsync($"/clients/{link.Groups[1].Value}");

        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Contains("Attached", await detail.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A project can be opened, and its list links to it.
    /// </summary>
    /// <remarks>
    /// The same gap the clients had: attachments were built for projects and
    /// the only page about them was a list with no way in.
    /// </remarks>
    [Fact]
    public async Task A_project_can_be_opened_from_the_list()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var work = scope.ServiceProvider.GetRequiredService<WorkService>();
            await work.BeginProjectAsync($"Openable {Guid.CreateVersion7():N}");
        }

        var browser = await SignedInAsync("projpm@jiranisokotech.co.ke", Roles.ProjectManager);

        var list = await browser.GetAsync("/projects");
        var html = await list.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        var link = System.Text.RegularExpressions.Regex.Match(
            html, @"href=""/projects/([0-9a-f-]{36})""");

        Assert.True(link.Success, "The project list offered no link to open a project.");

        var detail = await browser.GetAsync($"/projects/{link.Groups[1].Value}");

        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Contains("Attached", await detail.Content.ReadAsStringAsync());
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
