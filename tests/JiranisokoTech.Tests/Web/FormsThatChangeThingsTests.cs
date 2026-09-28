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

    /// <summary>
    /// Saying which client a project is for records it, and the client then counts it.
    /// </summary>
    /// <remarks>
    /// Before this there was no way to do it at all. <c>Project.ForClient</c> had no caller
    /// outside a test, so every project was for nobody and every client page said it had none —
    /// the first link of the chain from a client to its profitability, missing while the
    /// checklist said every link was there. The count is asserted as well as the row, because
    /// the count is what a person reading the client page actually sees.
    /// </remarks>
    [Fact]
    public async Task Saying_which_client_a_project_is_for_records_it()
    {
        var (browser, project, client) = await AProjectAndAClient();

        await PostClientAsync(browser, project, client.ToString());

        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var stored = await database.Projects.AsNoTracking().SingleAsync(one => one.Id == project);
        Assert.Equal(client, stored.ClientId);

        var clients = await scope.ServiceProvider
            .GetRequiredService<JiranisokoTech.Infrastructure.Business.BusinessQueries>()
            .ClientsAsync();
        Assert.Equal(1, clients.Single(one => one.Id == client).Projects);
    }

    /// <summary>
    /// Making a project internal again takes the client off it.
    /// </summary>
    /// <remarks>
    /// The direction the seeding fault gets wrong: the select is filled with the stored client
    /// when somebody arrives, and if that ran on the POST as well, the empty choice would be
    /// overwritten with the client it was meant to remove.
    /// </remarks>
    [Fact]
    public async Task Making_a_project_internal_again_takes_the_client_off_it()
    {
        var (browser, project, client) = await AProjectAndAClient();

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<WorkService>()
                .ForClientAsync(project, client);
        }

        await PostClientAsync(browser, project, string.Empty);

        using var after = factory.Services.CreateScope();
        var database = after.ServiceProvider.GetRequiredService<AppDbContext>();

        var stored = await database.Projects.AsNoTracking().SingleAsync(one => one.Id == project);
        Assert.Null(stored.ClientId);
    }

    private static async Task PostClientAsync(HttpClient browser, Guid project, string client)
    {
        var page = await browser.GetAsync($"/projects/{project}");

        var fields = HtmlForm.Fill(
            await page.Content.ReadAsStringAsync(),
            new Dictionary<string, string>
            {
                ["_handler"] = "client",
                ["Serving.ClientId"] = client,
            });

        var posted = await browser.PostAsync(
            $"/projects/{project}", new FormUrlEncodedContent(fields));

        // The page redirects to itself when the change is made and renders its refusal in place
        // when it is not, so a redirect is the page saying it did it.
        Assert.Equal(HttpStatusCode.Found, posted.StatusCode);
    }

    private async Task<(HttpClient Browser, Guid Project, Guid Client)> AProjectAndAClient()
    {
        var browser = await SignedInAsync("projects@jiranisokotech.co.ke", Roles.Administrator);

        using var scope = factory.Services.CreateScope();

        // The tail, not the head: a version 7 identifier starts with the time, so two made in the
        // same millisecond share their first eight characters and the project codes collide.
        var suffix = Guid.CreateVersion7().ToString("N")[^8..];

        var project = await scope.ServiceProvider.GetRequiredService<WorkService>()
            .BeginProjectAsync("Statements " + suffix);

        var client = await scope.ServiceProvider
            .GetRequiredService<JiranisokoTech.Application.Business.ClientService>()
            .TakeOnAsync("Acme " + suffix);

        return (browser, project.Id, client.Id);
    }

    /// <summary>
    /// Recording somebody's salary on their staff record records it.
    /// </summary>
    /// <remarks>
    /// Found by trying to run a payroll in the running application: the terms form answered
    /// with the success redirect and the salary column stayed empty. The staff record seeded
    /// its forms behind a <c>_filled</c> flag, which is false again on every POST, so the
    /// stored terms overwrote the posted ones before the handler read them. Payroll skips
    /// anybody with no salary recorded, so this left the pay run empty with no fault shown
    /// anywhere.
    /// </remarks>
    [Fact]
    public async Task Recording_a_salary_on_the_staff_record_records_it()
    {
        var browser = await SignedInAsync("salaries@jiranisokotech.co.ke", Roles.Administrator);

        Guid person;

        using (var scope = factory.Services.CreateScope())
        {
            var people = scope.ServiceProvider.GetRequiredService<PeopleService>();

            person = (await people.HireAsync(
                "Salaried " + Guid.CreateVersion7().ToString("N")[^8..],
                DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30))).Id;
        }

        await PostAsync(browser, $"/people/{person}", "terms", new()
        {
            ["Pay.Salary"] = "150000",
            ["Pay.Currency"] = "KES",
        });

        using var after = factory.Services.CreateScope();

        var stored = await after.ServiceProvider.GetRequiredService<AppDbContext>().Employees
            .AsNoTracking()
            .SingleAsync(one => one.Id == person);

        Assert.Equal(150_000_00, stored.Terms.SalaryMinorUnits);
    }

    /// <summary>
    /// Changing your own phone number on your profile changes it.
    /// </summary>
    /// <remarks>
    /// The same seeding fault as the staff record, on the page somebody edits themselves — and
    /// behind it a second one. With the seeding fixed this still failed, silently: the personal
    /// email box was empty, a browser posts an empty box as an empty string, and
    /// <c>[EmailAddress]</c> calls an empty string invalid. The form showed no message and saved
    /// nothing, so anybody without a personal email on file could not record a phone number.
    /// The personal email is deliberately left empty here, because that is the case that failed.
    /// </remarks>
    [Fact]
    public async Task Changing_your_own_phone_number_changes_it()
    {
        const string email = "profile-phone@jiranisokotech.co.ke";

        var browser = await SignedInAsync(email, Roles.Developer);

        Guid person;

        using (var scope = factory.Services.CreateScope())
        {
            var people = scope.ServiceProvider.GetRequiredService<PeopleService>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            person = (await people.HireAsync(
                "Profiled " + Guid.CreateVersion7().ToString("N")[^8..],
                DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30))).Id;

            await people.StartAsync(person);
            await people.LinkAccountAsync(person, (await users.FindByEmailAsync(email))!.Id);
        }

        await PostAsync(browser, "/my-profile", "details", new()
        {
            ["Input.Phone"] = "+254 700 000 123",
        });

        using var after = factory.Services.CreateScope();

        var stored = await after.ServiceProvider.GetRequiredService<AppDbContext>().Employees
            .AsNoTracking()
            .SingleAsync(one => one.Id == person);

        Assert.Equal("+254 700 000 123", stored.Details.Phone);
    }

    /// <summary>
    /// Recording what a candidate expects to be paid records it.
    /// </summary>
    /// <remarks>The same seeding fault again, on the candidate page.</remarks>
    [Fact]
    public async Task Recording_a_candidates_salary_expectation_records_it()
    {
        var browser = await SignedInAsync("candidates@jiranisokotech.co.ke", Roles.Administrator);

        var candidate = JiranisokoTech.Domain.Recruitment.Candidate.Of(
            "Grace Wanjiku",
            $"grace-{Guid.CreateVersion7().ToString("N")[^8..]}@example.com",
            null,
            DateTimeOffset.UtcNow);

        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            database.Candidates.Add(candidate);
            await database.SaveChangesAsync();
        }

        await PostAsync(browser, $"/hiring/candidates/{candidate.Id}", "expectation", new()
        {
            ["Expectation.Amount"] = "180000",
            ["Expectation.Currency"] = "KES",
        });

        using var after = factory.Services.CreateScope();

        var stored = await after.ServiceProvider.GetRequiredService<AppDbContext>().Candidates
            .AsNoTracking()
            .SingleAsync(one => one.Id == candidate.Id);

        Assert.Equal(180_000_00, stored.ExpectedSalaryMinorUnits);
    }

    /// <summary>
    /// Drafting pay for a period drafts that period.
    /// </summary>
    /// <remarks>
    /// The payroll page set its dates to last month on every request, the POST included, so the
    /// dates somebody typed were replaced before the handler read them: only the month just
    /// gone could ever be drafted, and asking for another drafted that one and said "Drafted".
    /// February 2024 because it is certainly not last month, and a leap-year February because
    /// its last day is the one a default would get wrong.
    /// </remarks>
    [Fact]
    public async Task Drafting_pay_for_a_period_drafts_that_period()
    {
        var browser = await SignedInAsync("payroll-periods@jiranisokotech.co.ke", Roles.Administrator);

        await PostAsync(browser, "/payroll", "draft", new()
        {
            ["Input.From"] = "2024-02-01",
            ["Input.To"] = "2024-02-29",
        });

        using var scope = factory.Services.CreateScope();

        Assert.True(await scope.ServiceProvider.GetRequiredService<AppDbContext>().PayRuns
            .AnyAsync(run => run.PeriodStart == new DateOnly(2024, 2, 1)
                && run.PeriodEnd == new DateOnly(2024, 2, 29)));
    }

    /// <summary>
    /// Setting a retention period sets it, and clearing it clears it.
    /// </summary>
    /// <remarks>
    /// Clearing is the case that matters. Blank means keep for ever, so the form cannot use
    /// "the box is empty" to mean "nothing was posted" the way the settings forms above it do —
    /// that would refill a cleared period with the stored one and report it saved.
    /// </remarks>
    [Fact]
    public async Task Setting_and_clearing_a_retention_period_is_recorded()
    {
        var browser = await SignedInAsync("retention@jiranisokotech.co.ke", Roles.Owner);

        await PostAsync(browser, "/settings", "retention", new()
        {
            ["Keeping.CandidateMonths"] = "12",
            ["Keeping.AuditYears"] = "10",
        });

        using (var scope = factory.Services.CreateScope())
        {
            var firm = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Settings
                .AsNoTracking()
                .SingleAsync();

            Assert.Equal(12, firm.CandidateRetentionMonths);
            Assert.Equal(10, firm.AuditRetentionYears);
        }

        await PostAsync(browser, "/settings", "retention", new()
        {
            ["Keeping.CandidateMonths"] = string.Empty,
            ["Keeping.AuditYears"] = "10",
        });

        using var after = factory.Services.CreateScope();

        var cleared = await after.ServiceProvider.GetRequiredService<AppDbContext>().Settings
            .AsNoTracking()
            .SingleAsync();

        Assert.Null(cleared.CandidateRetentionMonths);
        Assert.Equal(10, cleared.AuditRetentionYears);
    }

    private static async Task PostAsync(
        HttpClient browser, string path, string handler, Dictionary<string, string> values)
    {
        var page = await browser.GetAsync(path);

        values["_handler"] = handler;

        var fields = HtmlForm.Fill(await page.Content.ReadAsStringAsync(), values);

        await browser.PostAsync(path, new FormUrlEncodedContent(fields));
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
