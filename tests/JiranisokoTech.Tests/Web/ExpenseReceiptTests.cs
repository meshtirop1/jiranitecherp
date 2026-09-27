using System.Net;
using System.Net.Http.Headers;
using System.Text;
using JiranisokoTech.Application.People;
using JiranisokoTech.Domain.Documents;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Tests.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// A claim can carry its receipt, and the approver can open it.
/// </summary>
/// <remarks>
/// <b>These exist because no claim ever had a receipt.</b> The claim had a place for one and a
/// method to set it, the document store had a kind for it and a permission table naming it,
/// and nothing anywhere called any of them — no screen offered a file. Every claim in the
/// system was approved on the claimant's word, while the checklist marked expenses done.
///
/// Through the real multipart form, because the page is statically rendered and a file arrives
/// with the post or not at all; a test of the service would have passed the whole time.
/// </remarks>
public class ExpenseReceiptTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    private const string Password = "a-long-enough-password";

    [Fact]
    public async Task A_claim_made_with_a_receipt_keeps_it_and_the_approver_can_open_it()
    {
        var browser = await AClaimantAsync("claims-receipt@jiranisokotech.co.ke");
        var description = "Taxi to the Upper Hill site " + Suffix();

        var posted = await ClaimAsync(browser, description, ("receipt", "taxi-receipt.pdf"));

        Assert.Equal(HttpStatusCode.Found, posted.StatusCode);

        await factory.InScopeAsync(async services =>
        {
            var database = services.GetRequiredService<AppDbContext>();

            var claim = await database.Expenses
                .AsNoTracking()
                .SingleAsync(one => one.Description == description);

            Assert.True(claim.HasReceipt);
            Assert.Equal("taxi-receipt.pdf", claim.ReceiptFileName);

            var stored = await database.Attachments
                .AsNoTracking()
                .SingleAsync(one => one.Id == Guid.Parse(claim.ReceiptStoredName!));

            Assert.Equal(AttachedTo.ExpenseClaim, stored.Kind);
            Assert.Equal(claim.Id, stored.OwnerId);
        });

        // The approver's list links to it, and the link opens the file.
        var approver = await SignedInAsync("claims-approver@jiranisokotech.co.ke", Roles.Administrator);
        var waiting = await (await approver.GetAsync("/expenses/claims")).Content.ReadAsStringAsync();

        var link = System.Text.RegularExpressions.Regex.Match(
            waiting[waiting.IndexOf(description, StringComparison.Ordinal)..],
            "href=\"(/documents/[0-9a-f-]+)\"").Groups[1].Value;

        var download = await approver.GetAsync(link);

        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("a receipt", await download.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A file that would be refused leaves no claim behind.
    /// </summary>
    /// <remarks>
    /// Checked before the claim is made, because checked after, the refusal would leave a draft
    /// nobody meant to create sitting in the list with a Submit button beside it.
    /// </remarks>
    [Fact]
    public async Task A_refused_file_leaves_no_claim_behind()
    {
        var browser = await AClaimantAsync("claims-refused@jiranisokotech.co.ke");
        var description = "Lunch with the client " + Suffix();

        var posted = await ClaimAsync(browser, description, ("receipt", "receipt.exe"));

        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);

        await factory.InScopeAsync(async services =>
        {
            var database = services.GetRequiredService<AppDbContext>();

            Assert.False(await database.Expenses.AnyAsync(one => one.Description == description));
        });
    }

    private static async Task<HttpResponseMessage> ClaimAsync(
        HttpClient browser, string description, (string Field, string Name) file)
    {
        var page = await browser.GetAsync("/expenses");

        var fields = HtmlForm.Fill(
            await page.Content.ReadAsStringAsync(),
            new Dictionary<string, string>
            {
                ["_handler"] = "claim",
                ["Input.Description"] = description,
                ["Input.Amount"] = "850",
                ["Input.SpentOn"] = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
            });

        using var form = new MultipartFormDataContent();

        foreach (var (name, value) in fields)
        {
            form.Add(new StringContent(value), name);
        }

        var contents = new ByteArrayContent(Encoding.UTF8.GetBytes("a receipt"));
        contents.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(contents, file.Field, file.Name);

        return await browser.PostAsync("/expenses", form);
    }

    /// <summary>Somebody on the staff, signed in, who may claim expenses.</summary>
    private async Task<HttpClient> AClaimantAsync(string email)
    {
        var browser = await SignedInAsync(email, Roles.Developer);

        using var scope = factory.Services.CreateScope();

        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var account = await users.FindByEmailAsync(email);

        var people = scope.ServiceProvider.GetRequiredService<PeopleService>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var employee = await people.HireAsync("Claimant " + Suffix(), today.AddDays(-90));
        await people.StartAsync(employee.Id);
        await people.LinkAccountAsync(employee.Id, account!.Id);

        return browser;
    }

    private static string Suffix() => Guid.CreateVersion7().ToString("N")[^8..];

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
