using System.Net;
using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Application.People;
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

    private async Task<Guid> HireAsync(string name, string jobTitle)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<PeopleService>();

        var employee = await service.HireAsync(name, Monday, jobTitle: jobTitle);

        return employee.Id;
    }

    /// <summary>
    /// An account in a role, signed in through the real form.
    /// </summary>
    /// <remarks>
    /// Reuses the account when it is already there. The class shares one
    /// application, and a theory runs its method once per case — so creating
    /// unconditionally makes the second case fail on a duplicate address, which
    /// reads as a permission fault and is not one.
    /// </remarks>
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
