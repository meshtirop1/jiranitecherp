using System.Net;
using JiranisokoTech.Application.Business;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Tests.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// The trail, read back.
/// </summary>
/// <remarks>
/// It was write-only until now: recorded in the same transaction as every
/// change since the first week, and readable nowhere. The tests that existed
/// asserted rows were written, which is exactly half of what an audit trail is
/// for — and the half that cannot be checked by looking at the application.
/// </remarks>
public class AuditPageTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    private const string Password = "a-long-enough-password";

    [Fact]
    public async Task A_stranger_is_sent_to_sign_in()
    {
        using var browser = factory.CreateBrowser();

        var response = await browser.GetAsync("/audit");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("/sign-in", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task A_developer_cannot_read_the_trail()
    {
        var browser = await SignedInAsync("auditdev@jiranisokotech.co.ke", Roles.Developer);

        var response = await browser.GetAsync("/audit");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("/denied", response.Headers.Location!.OriginalString);
    }

    /// <summary>
    /// A change made through the application appears on the page.
    /// </summary>
    /// <remarks>
    /// End to end on purpose: a client is taken on through the real service,
    /// which writes its audit row in the same transaction, and the page is then
    /// asked for. Asserting on the query alone would have passed while the page
    /// rendered nothing, which is the state this system was already in.
    /// </remarks>
    [Fact]
    public async Task A_change_shows_up_on_the_trail()
    {
        var name = $"Acme {Guid.CreateVersion7():N}";

        using (var scope = factory.Services.CreateScope())
        {
            var clients = scope.ServiceProvider.GetRequiredService<ClientService>();

            await clients.TakeOnAsync(name);
        }

        var browser = await SignedInAsync("auditor@jiranisokotech.co.ke", Roles.Administrator);

        var response = await browser.GetAsync("/audit");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The client's name is in the "became" column of the entry recording it.
        Assert.Contains(name, html);
    }

    /// <summary>
    /// Filtering by kind narrows it, and the filter has an address.
    /// </summary>
    [Fact]
    public async Task The_trail_can_be_narrowed_to_one_kind_of_thing()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var clients = scope.ServiceProvider.GetRequiredService<ClientService>();

            await clients.TakeOnAsync($"Filtered {Guid.CreateVersion7():N}");
        }

        var browser = await SignedInAsync("auditor2@jiranisokotech.co.ke", Roles.Administrator);

        var matching = await browser.GetAsync("/audit?type=Client");
        var other = await browser.GetAsync("/audit?type=Invoice");

        Assert.Equal(HttpStatusCode.OK, matching.StatusCode);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);

        Assert.Contains("Filtered", await matching.Content.ReadAsStringAsync());
        Assert.DoesNotContain("Filtered", await other.Content.ReadAsStringAsync());
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
