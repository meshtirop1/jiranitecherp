using System.Net;
using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Application.People;
using JiranisokoTech.Application.Work;
using JiranisokoTech.Domain.People;
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
/// A form that changes a stored value actually changes it.
/// </summary>
/// <remarks>
/// <b>These exist because four pages reported success and changed nothing.</b>
///
/// A page that lets somebody change a stored value seeds the control with the current one, so
/// the select shows who the work is assigned to now. It has to stop doing that once a form has
/// been posted, because the posted model holds the new value. All four pages knew: each carried
/// a comment saying "only before the form has been posted", and each guarded the seeding with a
/// <c>_loaded</c> field set on the first pass.
///
/// Under static server rendering that guard does nothing. Every request is a new component
/// instance, so on the POST the field is false again, the seeding runs before the handler reads
/// the model, and the service is called with the value that was already stored. It does exactly
/// what it is told, which is nothing, and the page says "Assigned."
///
/// Found by assigning a work item in the running application, being told it had worked, and
/// looking at the row. No test could see it, because every test of these services called the
/// service. This one goes through the form.
/// </remarks>
public class FormsThatChangeThingsTests(ApplicationFactory factory)
    : IClassFixture<ApplicationFactory>
{
    private const string Password = "a-long-enough-password";

    /// <summary>
    /// Assigning a work item to somebody else assigns it to them.
    /// </summary>
    /// <remarks>
    /// The one that was found first. Asserted on the stored row rather than on the page,
    /// because the page said "Assigned." the entire time it was broken — the screen is exactly
    /// what could not be trusted.
    /// </remarks>
    [Fact]
    public async Task Assigning_work_to_somebody_else_assigns_it_to_them()
    {
        var (browser, item, brian) = await AWorkItemAssignedToSomebody();

        var page = await browser.GetAsync($"/work/{item}");

        var fields = HtmlForm.Fill(
            await page.Content.ReadAsStringAsync(),
            new Dictionary<string, string>
            {
                ["_handler"] = "assign",
                ["Assignment.AssigneeId"] = brian.ToString(),
            });

        var posted = await browser.PostAsync(
            $"/work/{item}", new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);

        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var stored = await database.WorkItems
            .AsNoTracking()
            .SingleAsync(one => one.Id == item);

        Assert.Equal(brian, stored.AssigneeId);
    }

    /// <summary>
    /// Taking a work item off everybody takes it off them.
    /// </summary>
    /// <remarks>
    /// The other direction, and it would have gone on failing after a fix that only compared
    /// the posted value against the stored one — unassigning posts an empty value, which is the
    /// case a "use the posted value when it is not empty" fix gets wrong.
    /// </remarks>
    [Fact]
    public async Task Taking_work_off_everybody_takes_it_off_them()
    {
        var (browser, item, _) = await AWorkItemAssignedToSomebody();

        var page = await browser.GetAsync($"/work/{item}");

        var fields = HtmlForm.Fill(
            await page.Content.ReadAsStringAsync(),
            new Dictionary<string, string>
            {
                ["_handler"] = "assign",
                ["Assignment.AssigneeId"] = string.Empty,
            });

        await browser.PostAsync($"/work/{item}", new FormUrlEncodedContent(fields));

        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var stored = await database.WorkItems
            .AsNoTracking()
            .SingleAsync(one => one.Id == item);

        Assert.Null(stored.AssigneeId);
    }

    /// <summary>A work item held by one person, and somebody else to give it to.</summary>
    private async Task<(HttpClient Browser, Guid Item, Guid Brian)>
        AWorkItemAssignedToSomebody()
    {
        var browser = await SignedInAsync("boards@jiranisokotech.co.ke", Roles.Administrator);

        using var scope = factory.Services.CreateScope();

        var people = scope.ServiceProvider.GetRequiredService<PeopleService>();
        var work = scope.ServiceProvider.GetRequiredService<WorkService>();

        var amina = await people.HireAsync(
            "Amina " + Guid.CreateVersion7().ToString("N")[..6],
            DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30));

        var brian = await people.HireAsync(
            "Brian " + Guid.CreateVersion7().ToString("N")[..6],
            DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30));

        // Work cannot be given to somebody who has not started, which is the service's rule and
        // not this test's business — so both of them start.
        await people.StartAsync(amina.Id);
        await people.StartAsync(brian.Id);

        var item = await work.RaiseAsync(
            "Statement PDF has the wrong logo", Guid.CreateVersion7());

        await work.AssignAsync(item.Id, amina.Id);

        return (browser, item.Id, brian.Id);
    }

    private async Task<HttpClient> SignedInAsync(string email, string role)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider
                .GetRequiredService<UserManager<ApplicationUser>>();

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
