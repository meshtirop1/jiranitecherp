using System.Net;
using JiranisokoTech.Application.Api;
using JiranisokoTech.Application.Business;
using JiranisokoTech.Domain.Clients;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Tests.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// What is typed into a form rendered once per row reaches the handler.
/// </summary>
/// <remarks>
/// <b>These exist because ten such forms dropped what was typed into them.</b> Each was named
/// after its row and bound by a fixed name, which can never match — see
/// <c>PerRowFormTests</c>, which reads the markup for the shape. These post the real forms, so
/// that the thing a person would notice is what is asserted: the key is revoked, the
/// opportunity moves, the note is kept.
///
/// Revoking an API key is the one that mattered most. The reason arrived empty, the page said
/// "Say why, so the trail explains itself" under the reason just typed, and the key stayed
/// live — so a key that had leaked could not be stopped from the screen built to stop it.
/// </remarks>
public class PerRowFormPostTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    private const string Password = "a-long-enough-password";

    [Fact]
    public async Task Revoking_an_api_key_revokes_it_with_the_reason_given()
    {
        var browser = await SignedInAsync("keys@jiranisokotech.co.ke");
        var name = "website " + Suffix();

        Guid id;

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApiKeyService>()
                .IssueAsync(name, ["clients.view"], null);

            id = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().ApiKeys
                .AsNoTracking()
                .SingleAsync(key => key.Name == name)).Id;
        }

        await PostAsync(browser, "/settings/keys", $"revoke-{id}", new()
        {
            ["Revoking.Reason"] = "pasted into a public issue",
        });

        using var after = factory.Services.CreateScope();

        var stored = await after.ServiceProvider.GetRequiredService<AppDbContext>().ApiKeys
            .AsNoTracking()
            .SingleAsync(key => key.Id == id);

        Assert.False(stored.IsLive);
        Assert.Equal("pasted into a public issue", stored.RevokedReason);
    }

    /// <summary>
    /// Moving an opportunity moves it to the stage chosen.
    /// </summary>
    /// <remarks>
    /// Proposed rather than anything else, because the model's default is Qualified: a test
    /// moving it to Qualified would pass with the binding broken, which is exactly how the
    /// broken form looked as though it worked.
    /// </remarks>
    [Fact]
    public async Task Moving_an_opportunity_moves_it_to_the_stage_chosen()
    {
        var browser = await SignedInAsync("pipeline@jiranisokotech.co.ke");
        var id = await AnOpportunityAsync();

        await PostAsync(browser, "/pipeline", $"move-{id}", new()
        {
            ["Moving.Stage"] = nameof(Stage.Proposed),
        });

        using var scope = factory.Services.CreateScope();

        var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Opportunities
            .AsNoTracking()
            .SingleAsync(one => one.Id == id);

        Assert.Equal(Stage.Proposed, stored.Stage);
    }

    [Fact]
    public async Task Writing_down_what_happened_on_an_opportunity_keeps_it()
    {
        var browser = await SignedInAsync("pipeline-notes@jiranisokotech.co.ke");
        var id = await AnOpportunityAsync();

        await PostAsync(browser, "/pipeline", $"note-{id}", new()
        {
            ["Noting.What"] = "They asked for the statement module first",
        });

        using var scope = factory.Services.CreateScope();

        var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Opportunities
            .AsNoTracking()
            .SingleAsync(one => one.Id == id);

        Assert.Contains(
            stored.Activities,
            activity => activity.What == "They asked for the statement module first");
    }

    private async Task<Guid> AnOpportunityAsync()
    {
        using var scope = factory.Services.CreateScope();

        var opened = await scope.ServiceProvider.GetRequiredService<OpportunityService>()
            .OpenAsync("Statements " + Suffix(), "A client portal for statements");

        return opened.Id;
    }

    /// <summary>
    /// Post one row's form as the browser would, and insist the page accepted it.
    /// </summary>
    /// <remarks>
    /// These pages redirect to themselves when the change is made and render their refusal in
    /// place when it is not, so anything but a redirect means the form was refused — and the
    /// refusal is the symptom being guarded against.
    /// </remarks>
    private static async Task PostAsync(
        HttpClient browser, string path, string handler, Dictionary<string, string> values)
    {
        var page = await browser.GetAsync(path);

        values["_handler"] = handler;

        var fields = HtmlForm.Fill(await page.Content.ReadAsStringAsync(), values);
        var posted = await browser.PostAsync(path, new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.Found, posted.StatusCode);
    }

    /// <remarks>
    /// The tail of the identifier, because a version 7 identifier starts with the time and two
    /// made in the same millisecond share their first characters.
    /// </remarks>
    private static string Suffix() => Guid.CreateVersion7().ToString("N")[^8..];

    private async Task<HttpClient> SignedInAsync(string email)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            if (await users.FindByEmailAsync(email) is null)
            {
                await factory.CreateAccountAsync(email, Password, email);
            }

            var stored = await users.FindByEmailAsync(email);

            if (!await users.IsInRoleAsync(stored!, Roles.Administrator))
            {
                await users.AddToRoleAsync(stored!, Roles.Administrator);
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
