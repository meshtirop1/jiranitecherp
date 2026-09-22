using System.Net;
using JiranisokoTech.Application.People;
using JiranisokoTech.Application.Recruitment;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Tests.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Web;

/// <summary>The hiring page, and who may open it.</summary>
public class HiringPageTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    private const string Password = "a-long-enough-password";
    private static readonly DateOnly Monday = new(2026, 10, 5);

    [Fact]
    public async Task A_stranger_is_sent_to_sign_in()
    {
        using var browser = factory.CreateBrowser();

        var response = await browser.GetAsync("/hiring");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("/sign-in", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task An_engineer_cannot_open_it()
    {
        var developer = await SignedInAsync("dev-hiring@jiranisokotech.co.ke", Roles.Developer, null);

        var response = await developer.GetAsync("/hiring");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("/denied", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task A_head_sees_the_requisitions_and_what_state_they_are_in()
    {
        var raiser = await StaffAsync("Charity Jepchirchir");

        await RaiseAsync(raiser, "Delivery Engineer");

        var browser = await SignedInAsync(
            "head-hiring@jiranisokotech.co.ke", Roles.DepartmentHead, raiser);

        var response = await browser.GetAsync("/hiring");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Delivery Engineer", html);
        Assert.Contains("Draft", html);
        Assert.Contains("0 of 1", html);
    }

    /// <summary>
    /// Raising a requisition puts somebody's name on it, so an account with no
    /// staff record has to be told rather than quietly failing.
    /// </summary>
    [Fact]
    public async Task An_unlinked_account_is_told_why_it_cannot_raise_one()
    {
        var browser = await SignedInAsync(
            "unlinked-hiring@jiranisokotech.co.ke", Roles.DepartmentHead, null);

        var page = await browser.GetAsync("/hiring");
        var fields = HtmlForm.Fill(
            await page.Content.ReadAsStringAsync(),
            new Dictionary<string, string>
            {
                ["Input.JobTitle"] = "Delivery Engineer",
                ["Input.Headcount"] = "1",
                ["Input.Justification"] = "Two projects starting and nobody free.",
            });

        var posted = await browser.PostAsync("/hiring", new FormUrlEncodedContent(fields));

        Assert.Contains("not linked to a staff record", await posted.Content.ReadAsStringAsync());
    }

    private async Task<Guid> StaffAsync(string name)
    {
        using var scope = factory.Services.CreateScope();
        var people = scope.ServiceProvider.GetRequiredService<PeopleService>();

        var person = await people.HireAsync(name, Monday);
        await people.StartAsync(person.Id);

        return person.Id;
    }

    private async Task RaiseAsync(Guid raisedBy, string title)
    {
        using var scope = factory.Services.CreateScope();
        var recruitment = scope.ServiceProvider.GetRequiredService<RecruitmentService>();

        await recruitment.RaiseRequisitionAsync(
            title, null, 1, "Two projects starting in November and nobody free.", raisedBy);
    }

    private async Task<HttpClient> SignedInAsync(string email, string role, Guid? employee)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            if (await users.FindByEmailAsync(email) is null)
            {
                await factory.CreateAccountAsync(email, Password, email);

                var stored = await users.FindByEmailAsync(email);
                await users.AddToRoleAsync(stored!, role);

                if (employee is { } person)
                {
                    var people = scope.ServiceProvider.GetRequiredService<PeopleService>();
                    await people.LinkAccountAsync(person, stored!.Id);
                }
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
