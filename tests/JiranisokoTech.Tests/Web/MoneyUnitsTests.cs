using System.Net;
using JiranisokoTech.Application.Settings;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Tests.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// A box that takes an amount takes it in whole currency, not in cents.
/// </summary>
/// <remarks>
/// <b>Three fields in this application were a hundred times out, and none of them failed
/// anything.</b> Money is stored in minor units throughout, which is right — a stored decimal
/// is a rounding argument waiting to happen, and <c>Money</c> is what the code works with. What
/// went wrong is that three input boxes bound straight to those columns with nothing on the
/// screen saying so:
///
/// <list type="bullet">
/// <item>An employee's salary. Typing 150000 for KES 150,000 recorded KES 1,500.00.</item>
/// <item>A candidate's expected salary, the same.</item>
/// <item>What an hour costs the firm — and that one is not a display value: every project's
/// cost and every project's margin is computed from it, so the whole of section 19 was out by
/// the same factor with nothing anywhere to say so.</item>
/// </list>
///
/// Each accepted the number, saved it, and reported success. The salary box even carried a hint
/// underneath warning about pay periods, which made it look as though somebody had thought
/// about the units.
///
/// The codebase already knew the right shape — <c>OneContract.razor</c> divides by a hundred on
/// the way in and multiplies on the way out — so this is a rule nobody wrote down rather than
/// one nobody knew.
/// </remarks>
public class MoneyUnitsTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    private const string Password = "a-long-enough-password";

    /// <summary>
    /// The hourly cost is stored as the amount somebody meant, times a hundred.
    /// </summary>
    /// <remarks>
    /// Driven through the real form rather than against the service, because the fault was
    /// never in the service. <c>SettingsService.CostAnHourAtAsync</c> has always taken minor
    /// units and always stored what it was given; the page handed it the wrong number.
    /// </remarks>
    [Fact]
    public async Task An_hourly_cost_typed_in_whole_shillings_is_stored_in_cents()
    {
        var browser = await SignedInAsync("money-units@jiranisokotech.co.ke", Roles.Owner);

        var page = await browser.GetAsync("/settings");
        var html = await page.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        var fields = HtmlForm.Fill(
            html,
            new Dictionary<string, string>
            {
                ["_handler"] = "billing",
                ["Billing.CostPerHour"] = "1200",
            });

        var posted = await browser.PostAsync("/settings", new FormUrlEncodedContent(fields));

        Assert.NotEqual(HttpStatusCode.BadRequest, posted.StatusCode);

        await factory.InScopeAsync(async services =>
        {
            var settings = services.GetRequiredService<SettingsService>();
            var firm = await settings.CurrentAsync();

            Assert.Equal(
                120_000,
                firm.StandardCostPerHourMinorUnits);
        });
    }

    /// <summary>
    /// And it comes back into the box as the amount that was typed.
    /// </summary>
    /// <remarks>
    /// The other half, and the half that would hide the first. A page that stored the right
    /// figure and then showed it back multiplied by a hundred would have somebody "correcting"
    /// it on the next visit, and the second save would be wrong in the other direction.
    /// </remarks>
    [Fact]
    public async Task An_hourly_cost_is_shown_back_as_the_amount_that_was_typed()
    {
        var browser = await SignedInAsync("money-round-trip@jiranisokotech.co.ke", Roles.Owner);

        await factory.InScopeAsync(async services =>
        {
            var settings = services.GetRequiredService<SettingsService>();

            await settings.CostAnHourAtAsync(120_000);
        });

        var html = await (await browser.GetAsync("/settings")).Content.ReadAsStringAsync();

        var fields = HtmlForm.Fill(html);

        Assert.True(
            fields.TryGetValue("Billing.CostPerHour", out var shown),
            "The billing form no longer carries a CostPerHour field.");

        Assert.Equal(1200m, decimal.Parse(shown));
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
