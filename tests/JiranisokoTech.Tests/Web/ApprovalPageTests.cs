using System.Net;
using JiranisokoTech.Application.Approvals;
using JiranisokoTech.Application.People;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Infrastructure.People;
using JiranisokoTech.Tests.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// The approvals page, which is where a stuck chain becomes visible.
/// </summary>
public class ApprovalPageTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    private const string Password = "a-long-enough-password";
    private static readonly DateOnly Monday = new(2026, 10, 5);

    [Fact]
    public async Task A_stranger_is_sent_to_sign_in()
    {
        using var browser = factory.CreateBrowser();

        var response = await browser.GetAsync("/approvals");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("/sign-in", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task An_engineer_cannot_open_it()
    {
        var developer = await SignedInAsync("dev-approve@jiranisokotech.co.ke", Roles.Developer, null);

        var response = await developer.GetAsync("/approvals");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("/denied", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task A_decision_waiting_on_somebody_appears_on_their_page()
    {
        var head = await StaffAsync("Charity Jepchirchir");
        var asker = await StaffAsync("Tirop Meshack");

        await RequestAsync(asker, head, "requisition.open");

        var browser = await SignedInAsync(
            "head-approve@jiranisokotech.co.ke", Roles.DepartmentHead, head);

        var response = await browser.GetAsync("/approvals");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("requisition open", html);
        Assert.Contains("Tirop Meshack", html);
    }

    /// <summary>
    /// Nobody is asked to decide before it is their turn, or they learn to
    /// ignore the page.
    /// </summary>
    /// <remarks>
    /// It does appear further down, in the list of everything still open, which
    /// is a different question and only shown to somebody who can oversee the
    /// lot. What must not happen is its being put in front of them as theirs to
    /// decide.
    /// </remarks>
    [Fact]
    public async Task Somebody_further_up_the_chain_is_not_asked_yet()
    {
        var lead = await StaffAsync("Purity");
        var director = await StaffAsync("Vincent Bungei");
        var asker = await StaffAsync("Duncan");

        await RequestAsync(asker, [lead, director], "expense.claim");

        var browser = await SignedInAsync(
            "director-approve@jiranisokotech.co.ke", Roles.DepartmentHead, director);

        var html = await (await browser.GetAsync("/approvals")).Content.ReadAsStringAsync();

        Assert.Contains("Nothing is waiting on you", html);
        Assert.DoesNotContain("Waiting on you", html);

        // And it is visible as something still open, because this reader can
        // see the whole board of them.
        Assert.Contains("Everything still open", html);
    }

    /// <summary>
    /// The oversight table is the thing somebody opens to find a chain that has
    /// sat on one person for three weeks, so it shows who it is with.
    /// </summary>
    [Fact]
    public async Task The_oversight_table_names_who_a_chain_is_waiting_on()
    {
        var lead = await StaffAsync("Precious");
        var asker = await StaffAsync("Tirop Meshack");

        await RequestAsync(asker, lead, "leave.request");

        var browser = await SignedInAsync(
            "oversight@jiranisokotech.co.ke", Roles.DepartmentHead, null);

        var html = await (await browser.GetAsync("/approvals")).Content.ReadAsStringAsync();

        Assert.Contains("leave request", html);
        Assert.Contains("Precious", html);
    }

    /// <summary>
    /// An account with no staff record cannot be waiting on anything, and the
    /// page says so rather than showing an empty list that reads as broken.
    /// </summary>
    [Fact]
    public async Task An_unlinked_account_is_told_why_there_is_nothing()
    {
        var browser = await SignedInAsync(
            "unlinked-approve@jiranisokotech.co.ke", Roles.DepartmentHead, null);

        var html = await (await browser.GetAsync("/approvals")).Content.ReadAsStringAsync();

        Assert.Contains("not linked to a staff record", html);
    }

    private async Task<Guid> StaffAsync(string name)
    {
        using var scope = factory.Services.CreateScope();
        var people = scope.ServiceProvider.GetRequiredService<PeopleService>();

        var person = await people.HireAsync(name, Monday);
        await people.StartAsync(person.Id);

        return person.Id;
    }

    private Task RequestAsync(Guid asker, Guid decider, string action) =>
        RequestAsync(asker, [decider], action);

    private async Task RequestAsync(Guid asker, IReadOnlyList<Guid> deciders, string action)
    {
        using var scope = factory.Services.CreateScope();
        var approvals = scope.ServiceProvider.GetRequiredService<ApprovalService>();

        await approvals.RequestAsync(
            "JobRequisition", Guid.CreateVersion7(), action, asker, deciders);
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
