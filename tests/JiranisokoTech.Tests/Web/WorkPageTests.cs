using System.Net;
using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Application.Work;
using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Tests.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// The board and the projects list, with their permissions enforced.
/// </summary>
public class WorkPageTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    private const string Password = "a-long-enough-password";

    [Theory]
    [InlineData("/work")]
    [InlineData("/projects")]
    public async Task A_stranger_is_sent_to_sign_in(string path)
    {
        using var browser = factory.CreateBrowser();

        var response = await browser.GetAsync(path);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("/sign-in", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task The_board_shows_work_in_the_column_it_is_in()
    {
        var project = await BeginAsync("Delivery note printer");

        await RaiseAsync("Draw the label layout", project, WorkItemStatus.Todo);
        await RaiseAsync("Mount the label printer", project, WorkItemStatus.InProgress);

        var browser = await SignedInAsync("board@jiranisokotech.co.ke", Roles.DepartmentHead);

        var response = await browser.GetAsync("/work");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Both are shown, and the one in progress appears after the To do
        // heading and after the In progress heading — that is, in the later
        // column.
        Assert.Contains("Draw the label layout", html);
        Assert.Contains("Mount the label printer", html);
        Assert.True(
            html.IndexOf("In progress", StringComparison.Ordinal)
            < html.IndexOf("Mount the label printer", StringComparison.Ordinal),
            "The in-progress item was not drawn under the In progress heading.");
    }

    /// <summary>
    /// Filtering by project is a link, so "show me what is left on this one" is
    /// something somebody can send to a colleague.
    /// </summary>
    [Fact]
    public async Task The_board_can_be_narrowed_to_one_project_by_its_address()
    {
        var mine = await BeginAsync("Printer rollout");
        var other = await BeginAsync("Website refresh");

        await RaiseAsync("On the printer", mine, WorkItemStatus.Todo);
        await RaiseAsync("On the website", other, WorkItemStatus.Todo);

        var browser = await SignedInAsync("filter@jiranisokotech.co.ke", Roles.DepartmentHead);

        var html = await (await browser.GetAsync($"/work?project={mine}")).Content.ReadAsStringAsync();

        Assert.Contains("On the printer", html);
        Assert.DoesNotContain("On the website", html);
    }

    /// <summary>
    /// An engineer can open the board. This is a delivery system and they are
    /// its main users; a board they cannot look at is not one.
    /// </summary>
    [Fact]
    public async Task An_engineer_can_open_the_board()
    {
        var developer = await SignedInAsync("dev-board@jiranisokotech.co.ke", Roles.Developer);

        var response = await developer.GetAsync("/work");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// But only at their own work. Holding tasks.view_own is permission to see
    /// what is yours, not what is everybody else's.
    /// </summary>
    [Fact]
    public async Task An_engineer_sees_only_their_own_work()
    {
        await RaiseAsync("Somebody elses job", null, WorkItemStatus.Todo);

        var developer = await SignedInAsync("dev-own@jiranisokotech.co.ke", Roles.Developer);

        var html = await (await developer.GetAsync("/work")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("Somebody elses job", html);
    }

    /// <summary>
    /// And cannot open a card that is not theirs, however they arrived at the
    /// address.
    /// </summary>
    [Fact]
    public async Task An_engineer_cannot_open_work_that_is_not_theirs()
    {
        var item = await RaiseAsync("Not for the engineer", null, WorkItemStatus.Todo);

        var developer = await SignedInAsync("dev-card@jiranisokotech.co.ke", Roles.Developer);

        var html = await (await developer.GetAsync($"/work/{item}")).Content.ReadAsStringAsync();

        Assert.Contains("Not yours to read", html);
        Assert.DoesNotContain("Assign it", html);
    }

    [Fact]
    public async Task A_head_can_open_and_move_any_work()
    {
        var item = await RaiseAsync("Mount the second printer", null, WorkItemStatus.Todo);

        var head = await SignedInAsync("head-board@jiranisokotech.co.ke", Roles.DepartmentHead);

        var html = await (await head.GetAsync($"/work/{item}")).Content.ReadAsStringAsync();

        Assert.Contains("Mount the second printer", html);
        Assert.Contains("Assign it", html);
    }

    /// <summary>
    /// Only the moves the state machine allows are offered. Drawing every
    /// column and refusing four of them after the click teaches people to
    /// distrust the buttons.
    /// </summary>
    [Fact]
    public async Task Only_the_moves_that_are_allowed_are_offered()
    {
        var item = await RaiseAsync("Choose the label stock", null, WorkItemStatus.Todo);

        var browser = await SignedInAsync("moves@jiranisokotech.co.ke", Roles.DepartmentHead);

        var html = await (await browser.GetAsync($"/work/{item}")).Content.ReadAsStringAsync();

        // From To do: in progress, blocked and cancelled — but never straight
        // to done.
        Assert.Contains("move-InProgress", html);
        Assert.Contains("move-Cancelled", html);
        Assert.DoesNotContain("move-Done", html);
        Assert.DoesNotContain("move-InReview", html);
    }

    /// <summary>
    /// Released work keeps a column, although it is finished.
    /// </summary>
    /// <remarks>
    /// Cancelled work is kept off the board because a board is about what is in
    /// front of people now. A release is the opposite case: it is the last thing
    /// that happens to a piece of work and the thing people ask about, and a head
    /// who releases four items and then sees no trace of any of them concludes
    /// the button did nothing.
    /// </remarks>
    [Fact]
    public async Task The_board_shows_a_column_for_released_work()
    {
        await AcceptedAsync("Cut the depot over to the new scanners", released: true);

        var head = await SignedInAsync("released@jiranisokotech.co.ke", Roles.DepartmentHead);

        var html = await (await head.GetAsync("/work")).Content.ReadAsStringAsync();

        Assert.Contains("Deployed", html);
        Assert.Contains("Cut the depot over to the new scanners", html);
        Assert.True(
            html.IndexOf("Deployed", StringComparison.Ordinal)
            < html.IndexOf("Cut the depot over to the new scanners", StringComparison.Ordinal),
            "The released item was not drawn under the Deployed heading.");
    }

    /// <summary>
    /// The gate, on one card, seen by two roles.
    /// </summary>
    /// <remarks>
    /// A delivery manager runs the board and may reopen this very item, so the
    /// page is not refusing them for want of reaching it — the release is the one
    /// move it does not offer them. Asserted through the page rather than only
    /// against the permission function, because the page is where the blanket
    /// "anybody who runs the board may move anything" rule used to live.
    /// </remarks>
    [Fact]
    public async Task Only_a_head_is_offered_the_release()
    {
        var item = await AcceptedAsync("Publish the delivery note template");

        var head = await SignedInAsync("release-head@jiranisokotech.co.ke", Roles.DepartmentHead);
        var forHead = await (await head.GetAsync($"/work/{item}")).Content.ReadAsStringAsync();

        Assert.Contains("move-Deployed", forHead);

        var manager = await SignedInAsync("release-pm@jiranisokotech.co.ke", Roles.ProjectManager);
        var forManager = await (await manager.GetAsync($"/work/{item}")).Content.ReadAsStringAsync();

        Assert.Contains("move-InProgress", forManager);
        Assert.DoesNotContain("move-Deployed", forManager);
    }

    /// <summary>Work walked along to accepted, and optionally out of the door.</summary>
    private async Task<Guid> AcceptedAsync(string title, bool released = false)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WorkService>();

        var item = await service.RaiseAsync(title, Guid.CreateVersion7(), null);

        await service.MoveAsync(item.Id, WorkItemStatus.InProgress);
        await service.MoveAsync(item.Id, WorkItemStatus.InReview);
        await service.MoveAsync(item.Id, WorkItemStatus.Done);

        if (released)
        {
            await service.MoveAsync(item.Id, WorkItemStatus.Deployed);
        }

        return item.Id;
    }

    private async Task<Guid> BeginAsync(string name)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WorkService>();

        var project = await service.BeginProjectAsync(name);
        await service.ActivateProjectAsync(project.Id);

        return project.Id;
    }

    private async Task<Guid> RaiseAsync(string title, Guid? projectId, WorkItemStatus status)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WorkService>();

        var item = await service.RaiseAsync(title, Guid.CreateVersion7(), projectId);

        if (status != WorkItemStatus.Todo)
        {
            await service.MoveAsync(item.Id, status);
        }

        return item.Id;
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
