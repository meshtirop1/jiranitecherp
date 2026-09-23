using System.Net;
using System.Text;
using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Application.Documents;
using JiranisokoTech.Application.People;
using JiranisokoTech.Domain.Documents;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Tests.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// The People screens, over HTTP, with the permissions actually enforced.
/// </summary>
/// <remarks>
/// The permission matrix is tested on its own elsewhere; what is unproven until
/// here is whether the pages are attached to it. An <c>[Authorize]</c> attribute
/// with a policy name that no provider builds, or a page that forgot the
/// attribute entirely, looks exactly like a working page to everything except a
/// request from somebody who should not have got in.
/// </remarks>
public class PeoplePageTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    private const string Password = "a-long-enough-password";
    private static readonly DateOnly Monday = new(2026, 10, 5);

    [Theory]
    [InlineData("/people")]
    [InlineData("/departments")]
    [InlineData("/org-chart")]
    public async Task A_stranger_is_sent_to_sign_in(string path)
    {
        using var browser = factory.CreateBrowser();

        var response = await browser.GetAsync(path);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("/sign-in", response.Headers.Location!.OriginalString);
    }

    /// <summary>
    /// A developer holds none of the People permissions, so these pages must
    /// refuse them — signed in or not.
    /// </summary>
    [Theory]
    [InlineData("/people")]
    [InlineData("/departments")]
    public async Task Somebody_without_the_permission_is_refused(string path)
    {
        var browser = await SignedInAsync("developer@jiranisokotech.co.ke", Roles.Developer);

        var response = await browser.GetAsync(path);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("/denied", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Hr_can_read_the_roster_and_sees_who_is_on_it()
    {
        await HireAsync("Vincent Bungei", "Managing Director");

        var browser = await SignedInAsync("hr-roster@jiranisokotech.co.ke", Roles.HumanResources);

        var response = await browser.GetAsync("/people");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Vincent Bungei", html);
        Assert.Contains("Managing Director", html);
    }

    /// <summary>
    /// Somebody who may read the roster but not change it must not be shown the
    /// controls for changing it. Hiding them is not the protection — the page
    /// checks the permission too — but showing a door that refuses everybody who
    /// opens it is its own kind of broken.
    /// </summary>
    [Fact]
    public async Task Somebody_who_may_only_read_is_not_shown_the_controls()
    {
        var person = await HireAsync("Purity", "Operations");

        var reader = await SignedInAsync("reader@jiranisokotech.co.ke", Roles.ProjectManager);

        var response = await reader.GetAsync($"/people/{person}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Purity", html);
        Assert.DoesNotContain("Record that they have left", html);
    }

    [Fact]
    public async Task Hr_is_shown_the_controls()
    {
        var person = await HireAsync("Duncan", "Engineer");

        var browser = await SignedInAsync("hr-controls@jiranisokotech.co.ke", Roles.HumanResources);

        var html = await (await browser.GetAsync($"/people/{person}")).Content.ReadAsStringAsync();

        Assert.Contains("Record that they have left", html);
    }

    [Fact]
    public async Task The_organisation_reads_as_a_tree()
    {
        Guid director;
        Guid report;

        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<PeopleService>();

            var top = await service.HireAsync("Charity Jepchirchir", Monday, jobTitle: "Director");
            var under = await service.HireAsync(
                "Tirop Meshack", Monday, jobTitle: "Engineer", reportsToId: top.Id);

            director = top.Id;
            report = under.Id;
        }

        var browser = await SignedInAsync("chart@jiranisokotech.co.ke", Roles.HumanResources);

        var html = await (await browser.GetAsync("/org-chart")).Content.ReadAsStringAsync();

        Assert.Contains("Charity Jepchirchir", html);
        Assert.Contains("Tirop Meshack", html);

        // The report is drawn underneath the director rather than beside them.
        Assert.True(
            html.IndexOf($"/people/{director}", StringComparison.Ordinal)
            < html.IndexOf($"/people/{report}", StringComparison.Ordinal),
            "The report was not drawn below the person they answer to.");
    }

    /// <summary>
    /// A personnel file is not part of the roster.
    /// </summary>
    /// <remarks>
    /// The page asks for employees.view, which every delivery manager and
    /// department head holds so they can see who is on the team. What hangs off a
    /// person is a contract with a salary on it, a disciplinary letter, a
    /// passport scan — so the section is drawn for employees.manage and for the
    /// person the record is about, and this is the case proving that reading the
    /// roster is not enough. The endpoint refuses them the bytes as well; this
    /// only asserts they are not shown a link they cannot follow.
    /// </remarks>
    [Fact]
    public async Task Somebody_who_may_only_read_the_roster_is_not_shown_the_personnel_file()
    {
        var person = await HireAsync("Naliaka Wekesa", "Analyst");
        await AttachAsync(person, "disciplinary-letter.pdf");

        var reader = await SignedInAsync("file-reader@jiranisokotech.co.ke", Roles.ProjectManager);

        var html = await (await reader.GetAsync($"/people/{person}")).Content.ReadAsStringAsync();

        Assert.Contains("Naliaka Wekesa", html);
        Assert.DoesNotContain("disciplinary-letter.pdf", html);
        Assert.DoesNotContain("identity document belongs here", html);
    }

    [Fact]
    public async Task Hr_is_shown_what_is_attached_and_the_form_for_attaching_more()
    {
        var person = await HireAsync("Sylvia Wanjiru", "Accountant");
        await AttachAsync(person, "employment-contract.pdf");

        var browser = await SignedInAsync("file-hr@jiranisokotech.co.ke", Roles.HumanResources);

        var html = await (await browser.GetAsync($"/people/{person}")).Content.ReadAsStringAsync();

        Assert.Contains("employment-contract.pdf", html);

        // A plain file input, not InputFile: this page is statically rendered, so
        // there is no circuit for InputFile or @onchange to run on.
        Assert.Contains("name=\"document\"", html);
    }

    /// <summary>
    /// Your own file on your own record, and no form for adding to it.
    /// </summary>
    /// <remarks>
    /// A delivery manager here, because they hold employees.view and not
    /// employees.manage — so they get through the door, read what is in their own
    /// file, and are offered no way to put anything in it. Reading your own
    /// contract is not permission to file things about yourself.
    /// </remarks>
    [Fact]
    public async Task A_person_is_shown_their_own_file_but_not_the_form_for_adding_to_it()
    {
        var me = await HireAsync("Eunice Wairimu", "Delivery Manager");
        await AttachAsync(me, "my-contract.pdf");

        var browser = await SignedInAsync(
            "file-mine@jiranisokotech.co.ke", Roles.ProjectManager, staffRecord: me);

        var html = await (await browser.GetAsync($"/people/{me}")).Content.ReadAsStringAsync();

        Assert.Contains("my-contract.pdf", html);
        Assert.DoesNotContain("name=\"document\"", html);
    }

    private async Task<Guid> HireAsync(string name, string jobTitle)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<PeopleService>();

        var employee = await service.HireAsync(name, Monday, jobTitle: jobTitle);

        return employee.Id;
    }

    private async Task AttachAsync(Guid employeeId, string fileName)
    {
        using var scope = factory.Services.CreateScope();
        var documents = scope.ServiceProvider.GetRequiredService<DocumentService>();

        await using var contents = new MemoryStream(Encoding.UTF8.GetBytes("a personnel document"));

        await documents.AttachAsync(
            AttachedTo.Employee, employeeId, contents, fileName, 20, null);
    }

    /// <summary>
    /// An account in a role, optionally linked to a staff record, signed in
    /// through the real form.
    /// </summary>
    /// <remarks>
    /// Reuses the account when it is already there. The class shares one
    /// application, and a theory runs its method once per case — so creating
    /// unconditionally makes the second case fail on a duplicate address, which
    /// reads as a permission fault and is not one. The link is made only
    /// alongside the creation for the same reason: an employee refuses a second
    /// account, deliberately, so that the change is never a typo.
    /// </remarks>
    private async Task<HttpClient> SignedInAsync(
        string email, string role, Guid? staffRecord = null)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            if (await users.FindByEmailAsync(email) is null)
            {
                await factory.CreateAccountAsync(email, Password, email);

                if (staffRecord is { } person)
                {
                    var people = scope.ServiceProvider.GetRequiredService<PeopleService>();
                    var created = await users.FindByEmailAsync(email);

                    await people.LinkAccountAsync(person, created!.Id);
                }
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
