using System.Net;
using System.Text.Json;
using JiranisokoTech.Application.People;
using JiranisokoTech.Application.Privacy;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Privacy;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Tests.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.People;

/// <summary>
/// A subject access request can be answered with one file, and reads of what is sensitive are
/// recorded.
/// </summary>
/// <remarks>
/// Section 55: data export and access logging. Until this, answering an access request meant
/// copying a dozen screens into a letter, and the audit trail recorded every change to a
/// person's salary and nothing about who had read it.
/// </remarks>
public class SubjectAccessTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    private const string Password = "a-long-enough-password";

    [Fact]
    public async Task An_access_request_is_answered_with_everything_held_and_the_export_is_recorded()
    {
        var browser = await SignedInAsync("dpo@jiranisokotech.co.ke");
        var (employee, request) = await AnEmployeeAndARequestAsync(PrivacyAsk.Access);

        var response = await browser.GetAsync($"/privacy/requests/{request}/export");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var text = await response.Content.ReadAsStringAsync();
        using var parsed = JsonDocument.Parse(text);

        // What was recorded about them is in it, including what a colleague is never shown.
        Assert.Contains("+254 722 000 111", text);
        Assert.Contains("A-12345678", text);
        Assert.Contains("15000000", text);
        Assert.True(parsed.RootElement.GetProperty("held").TryGetProperty("staffRecord", out _));

        // And it is on the trail that somebody took it.
        await factory.InScopeAsync(async services =>
        {
            Assert.True(await services.GetRequiredService<AppDbContext>().AuditEntries
                .AnyAsync(entry => entry.SubjectId == request && entry.Action == "privacy_request.exported"));
        });

        _ = employee;
    }

    /// <summary>An erasure request is not a request to be sent the record.</summary>
    [Fact]
    public async Task An_erasure_request_cannot_be_exported()
    {
        var browser = await SignedInAsync("dpo-erasure@jiranisokotech.co.ke");
        var (_, request) = await AnEmployeeAndARequestAsync(PrivacyAsk.Erasure);

        var response = await browser.GetAsync($"/privacy/requests/{request}/export");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Opening a staff record with the pay section on it is recorded against that person.
    /// </summary>
    [Fact]
    public async Task Seeing_somebodys_pay_is_recorded()
    {
        var browser = await SignedInAsync("payroll-viewer@jiranisokotech.co.ke");
        var (employee, _) = await AnEmployeeAndARequestAsync(PrivacyAsk.Access);

        var page = await browser.GetAsync($"/people/{employee}");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        await factory.InScopeAsync(async services =>
        {
            var viewed = await services.GetRequiredService<AppDbContext>().AuditEntries
                .Where(entry => entry.SubjectId == employee && entry.Action == "employee.pay_viewed")
                .ToListAsync();

            var one = Assert.Single(viewed);
            Assert.Equal("payroll-viewer@jiranisokotech.co.ke", one.ActorName);
        });
    }

    /// <summary>
    /// Somebody can keep their personal number to HR, and a department head is then not shown it.
    /// </summary>
    /// <remarks>
    /// Section 55's privacy settings. Through the real profile form, then the real staff record
    /// read as two different people, because the rule is about who is looking.
    /// </remarks>
    [Fact]
    public async Task Keeping_contact_details_to_HR_hides_them_from_other_managers()
    {
        const string email = "private-person@jiranisokotech.co.ke";
        var person = await SignedInAsync(email, Roles.Developer);
        var (employee, _) = await AnEmployeeAndARequestAsync(PrivacyAsk.Access);

        await factory.InScopeAsync(async services =>
        {
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            await services.GetRequiredService<PeopleService>()
                .LinkAccountAsync(employee, (await users.FindByEmailAsync(email))!.Id);
        });

        var profile = await person.GetAsync("/my-profile");
        await person.PostAsync("/my-profile", new FormUrlEncodedContent(HtmlForm.Fill(
            await profile.Content.ReadAsStringAsync(),
            new Dictionary<string, string> { ["_handler"] = "privacy", ["Privately.Keep"] = "true" })));

        var head = await SignedInAsync("a-department-head@jiranisokotech.co.ke", Roles.DepartmentHead);
        var asHead = await (await head.GetAsync($"/people/{employee}")).Content.ReadAsStringAsync();

        // The digits only: a page writes the plus sign as "&#x2B;", so asserting the whole
        // number is absent passes whatever the page shows.
        Assert.DoesNotContain("722 000 111", asHead);
        Assert.Contains("Kept to HR at their request", asHead);

        var hr = await SignedInAsync("an-hr-officer@jiranisokotech.co.ke", Roles.HumanResources);
        var asHr = await (await hr.GetAsync($"/people/{employee}")).Content.ReadAsStringAsync();

        Assert.Contains("722 000 111", asHr);
    }

    private async Task<(Guid Employee, Guid Request)> AnEmployeeAndARequestAsync(PrivacyAsk ask)
    {
        Guid employee = default;
        Guid request = default;

        await factory.InScopeAsync(async services =>
        {
            var people = services.GetRequiredService<PeopleService>();

            var person = await people.HireAsync(
                "Subject " + Guid.CreateVersion7().ToString("N")[^8..],
                DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-100));

            await people.StartAsync(person.Id);
            await people.RecordDetailsAsync(person.Id, person.Details with
            {
                Phone = "+254 722 000 111",
                NationalId = "A-12345678",
            });
            await people.AgreeTermsAsync(person.Id, new EmploymentTerms
            {
                SalaryMinorUnits = 150_000_00,
                SalaryCurrency = "KES",
                Frequency = PayFrequency.Monthly,
            });

            var received = await services.GetRequiredService<PrivacyService>().ReceiveAsync(
                ask, SubjectKind.Employee, person.FullName,
                DateOnly.FromDateTime(DateTime.UtcNow), person.Id);

            employee = person.Id;
            request = received.Id;
        });

        return (employee, request);
    }

    private Task<HttpClient> SignedInAsync(string email) => SignedInAsync(email, Roles.Owner);

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

        await browser.PostAsync("/sign-in", new FormUrlEncodedContent(HtmlForm.Fill(
            await form.Content.ReadAsStringAsync(),
            new Dictionary<string, string>
            {
                ["Input.Email"] = email,
                ["Input.Password"] = Password,
            })));

        return browser;
    }
}
