using JiranisokoTech.Application.People;
using JiranisokoTech.Application.Work;
using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Infrastructure.People;
using JiranisokoTech.Tests.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// The page somebody opens to find out what to do next.
/// </summary>
public class HomePageTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    private const string Password = "a-long-enough-password";
    private static readonly DateOnly Monday = new(2026, 10, 5);

    [Fact]
    public async Task It_shows_what_is_assigned_to_the_person_reading_it()
    {
        var browser = await SignedInAsync("mine@jiranisokotech.co.ke", "Tirop Meshack");

        await AssignAsync("mine@jiranisokotech.co.ke", "Wire up the label printer");

        var html = await (await browser.GetAsync("/")).Content.ReadAsStringAsync();

        Assert.Contains("Tirop Meshack", html);
        Assert.Contains("Wire up the label printer", html);
    }

    /// <summary>
    /// Somebody else's work is not on it. This is the page for what is in front
    /// of one person.
    /// </summary>
    [Fact]
    public async Task It_does_not_show_anybody_elses_work()
    {
        var browser = await SignedInAsync("only-mine@jiranisokotech.co.ke", "Purity");

        await AssignAsync("only-mine@jiranisokotech.co.ke", "Check the label stock");
        await RaiseAsync("Somebody elses errand");

        var html = await (await browser.GetAsync("/")).Content.ReadAsStringAsync();

        Assert.Contains("Check the label stock", html);
        Assert.DoesNotContain("Somebody elses errand", html);
    }

    /// <summary>
    /// Finished work is not on it either. It belongs on the board, where
    /// somebody goes looking for it.
    /// </summary>
    [Fact]
    public async Task Finished_work_drops_off_it()
    {
        var browser = await SignedInAsync("finished@jiranisokotech.co.ke", "Duncan");

        var item = await AssignAsync("finished@jiranisokotech.co.ke", "Fit the spare drum");

        using (var scope = factory.Services.CreateScope())
        {
            var work = scope.ServiceProvider.GetRequiredService<WorkService>();

            await work.MoveAsync(item, WorkItemStatus.InProgress);
            await work.MoveAsync(item, WorkItemStatus.InReview);
            await work.MoveAsync(item, WorkItemStatus.Done);
        }

        var html = await (await browser.GetAsync("/")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("Fit the spare drum", html);
    }

    /// <summary>
    /// An account with no staff record is an ordinary state, not an error, and
    /// the page says so rather than showing an empty list that looks broken.
    /// </summary>
    [Fact]
    public async Task An_unlinked_account_is_told_why_there_is_nothing()
    {
        var browser = await SignedInAsync("unlinked@jiranisokotech.co.ke", employee: null);

        var html = await (await browser.GetAsync("/")).Content.ReadAsStringAsync();

        Assert.Contains("not linked to a staff record", html);
    }

    private async Task<Guid> RaiseAsync(string title)
    {
        using var scope = factory.Services.CreateScope();
        var work = scope.ServiceProvider.GetRequiredService<WorkService>();

        var item = await work.RaiseAsync(title, Guid.CreateVersion7());

        return item.Id;
    }

    private async Task<Guid> AssignAsync(string email, string title)
    {
        using var scope = factory.Services.CreateScope();

        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var work = scope.ServiceProvider.GetRequiredService<WorkService>();
        var people = scope.ServiceProvider.GetRequiredService<PeopleQueries>();

        var account = await users.FindByEmailAsync(email);
        var employee = await people.EmployeeForAccountAsync(account!.Id);

        var item = await work.RaiseAsync(title, Guid.CreateVersion7());
        await work.AssignAsync(item.Id, employee);

        return item.Id;
    }

    /// <summary>
    /// An account, optionally with a staff record linked to it, signed in.
    /// </summary>
    private async Task<HttpClient> SignedInAsync(string email, string? employee)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            if (await users.FindByEmailAsync(email) is null)
            {
                await factory.CreateAccountAsync(email, Password, employee ?? email);

                var stored = await users.FindByEmailAsync(email);
                await users.AddToRoleAsync(stored!, Roles.Developer);

                if (employee is not null)
                {
                    var people = scope.ServiceProvider.GetRequiredService<PeopleService>();

                    var person = await people.HireAsync(employee, Monday);
                    await people.StartAsync(person.Id);
                    await people.LinkAccountAsync(person.Id, stored!.Id);
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
