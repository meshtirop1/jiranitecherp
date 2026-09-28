using System.Net;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Tests.Identity;
using JiranisokoTech.Web.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// Section 28, through the pages: the security centre, the access review, and what an
/// administrator can now do to an account that looks wrong.
/// </summary>
/// <remarks>
/// Every one of these posts or reads the real page, because the parts most likely to be wrong
/// are the parts a service test cannot see — a checkbox list read from the posted form, and
/// what the page shows to whom.
/// </remarks>
public class SecurityCentreTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    private const string Password = "a-long-enough-password";

    /// <summary>
    /// An administrator can see somebody else's sign-ins, including a guess at an address
    /// that matches nobody.
    /// </summary>
    /// <remarks>
    /// Until this, every sign-in was visible only to the person it belonged to, and an attempt
    /// against an address with no account was visible to nobody at all.
    /// </remarks>
    [Fact]
    public async Task An_administrator_sees_everybodys_sign_ins_including_guesses()
    {
        await SignedInAsync("someone-else@jiranisokotech.co.ke", Roles.Developer);

        var guesser = factory.CreateBrowser();
        var form = await guesser.GetAsync("/sign-in");
        await guesser.PostAsync("/sign-in", new FormUrlEncodedContent(HtmlForm.Fill(
            await form.Content.ReadAsStringAsync(),
            new Dictionary<string, string>
            {
                ["Input.Email"] = "nobody-by-that-name@jiranisokotech.co.ke",
                ["Input.Password"] = "a-guess-at-a-password",
            })));

        var admin = await SignedInAsync("security-admin@jiranisokotech.co.ke", Roles.Administrator);
        var page = await admin.GetAsync("/security");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        var html = await page.Content.ReadAsStringAsync();

        Assert.Contains("someone-else@jiranisokotech.co.ke", html);
        Assert.Contains("nobody-by-that-name@jiranisokotech.co.ke", html);
        Assert.Contains("no such account", html);
    }

    /// <summary>
    /// HR reads the trail and does not read everybody's sign-ins.
    /// </summary>
    [Fact]
    public async Task Holding_the_trail_is_not_holding_the_security_centre()
    {
        var hr = await SignedInAsync("security-hr@jiranisokotech.co.ke", Roles.HumanResources);

        var page = await hr.GetAsync("/security");
        var html = await page.Content.ReadAsStringAsync();

        Assert.DoesNotContain("Address tried", html);
    }

    /// <summary>
    /// A role granted shows as a permission change, with who granted it.
    /// </summary>
    [Fact]
    public async Task A_role_granted_shows_as_a_permission_change()
    {
        var admin = await SignedInAsync("granting-admin@jiranisokotech.co.ke", Roles.Administrator);
        var target = await AccountAsync("given-a-role@jiranisokotech.co.ke");

        await PostAsync(admin, $"/accounts/{target}", $"role-{Roles.ProjectManager}");

        var html = await (await admin.GetAsync("/security")).Content.ReadAsStringAsync();

        Assert.Contains("Granted project manager", html);
    }

    /// <summary>
    /// A review withdraws the ticked accounts, keeps the others, and records every one as it
    /// stood.
    /// </summary>
    /// <remarks>
    /// The reviewer ticks their own account as well, which the account page refuses. The
    /// review must still be recorded, say so, and record that account as kept — a review that
    /// claimed a withdrawal that had not happened would be worse than none.
    /// </remarks>
    [Fact]
    public async Task A_review_withdraws_what_was_ticked_and_records_everything()
    {
        const string reviewer = "reviewing-admin@jiranisokotech.co.ke";
        var admin = await SignedInAsync(reviewer, Roles.Administrator);
        var stays = await AccountAsync("stays@jiranisokotech.co.ke");
        var goes = await AccountAsync("goes@jiranisokotech.co.ke");
        var self = await IdOfAsync(reviewer);

        var form = await admin.GetAsync("/security/review");
        var fields = HtmlForm.Fill(
            await form.Content.ReadAsStringAsync(),
            new Dictionary<string, string>
            {
                ["_handler"] = "access-review",
                ["Review.Note"] = "Checked against the September staff list.",
            }).ToList();

        fields.Add(new("withdraw", goes.ToString()));
        fields.Add(new("withdraw", self.ToString()));

        var posted = await admin.PostAsync("/security/review", new FormUrlEncodedContent(fields));
        var html = await posted.Content.ReadAsStringAsync();

        Assert.Contains("not withdrawn", html);

        await factory.InScopeAsync(async services =>
        {
            var database = services.GetRequiredService<AppDbContext>();

            Assert.False((await database.Users.SingleAsync(user => user.Id == goes)).IsActive);
            Assert.True((await database.Users.SingleAsync(user => user.Id == stays)).IsActive);
            Assert.True((await database.Users.SingleAsync(user => user.Id == self)).IsActive);

            var review = await database.Set<AccessReview>()
                .SingleAsync(one => one.Note == "Checked against the September staff list.");

            Assert.False(review.Lines.Single(line => line.AccountId == goes).Kept);
            Assert.True(review.Lines.Single(line => line.AccountId == stays).Kept);
            Assert.True(review.Lines.Single(line => line.AccountId == self).Kept);
            Assert.Contains("administrator", review.Lines.Single(line => line.AccountId == self).Roles);
            Assert.Equal(reviewer, review.ReviewerName);
        });
    }

    /// <summary>
    /// An administrator can end somebody's sessions, and the trail says they did.
    /// </summary>
    /// <remarks>
    /// The only column that moves is the security stamp, which the trail excludes, so without
    /// the entry written by hand this would have left no mark at all.
    /// </remarks>
    [Fact]
    public async Task Ending_somebodys_sessions_rolls_their_stamp_and_is_on_the_trail()
    {
        var admin = await SignedInAsync("ending-admin@jiranisokotech.co.ke", Roles.Administrator);
        var target = await AccountAsync("sessions-ended@jiranisokotech.co.ke");

        var before = await StampAsync(target);

        await PostAsync(admin, $"/accounts/{target}", "end-sessions");

        Assert.NotEqual(before, await StampAsync(target));

        await factory.InScopeAsync(async services =>
        {
            var entry = await services.GetRequiredService<AppDbContext>().AuditEntries
                .SingleAsync(one => one.SubjectId == target && one.Action == "account.sessions_ended");

            Assert.Equal("ending-admin@jiranisokotech.co.ke", entry.ActorName);
        });
    }

    /// <summary>
    /// A lost phone with the recovery codes lost too is no longer the end of an account.
    /// </summary>
    [Fact]
    public async Task An_administrator_can_turn_off_somebodys_second_factor()
    {
        var admin = await SignedInAsync("factor-admin@jiranisokotech.co.ke", Roles.Administrator);
        var target = await AccountAsync("lost-phone@jiranisokotech.co.ke");

        await factory.InScopeAsync(async services =>
        {
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(target.ToString()))!;

            await users.ResetAuthenticatorKeyAsync(user);
            await users.SetTwoFactorEnabledAsync(user, true);
        });

        await PostAsync(admin, $"/accounts/{target}", "second-factor");

        await factory.InScopeAsync(async services =>
        {
            var user = await services.GetRequiredService<AppDbContext>().Users
                .SingleAsync(one => one.Id == target);

            Assert.False(user.TwoFactorEnabled);
        });
    }

    /// <summary>
    /// A secret is traced to where it came from, a settings file is called out, and the value
    /// never leaves.
    /// </summary>
    [Fact]
    public void Secrets_are_traced_to_their_source_and_never_shown()
    {
        var file = Path.Combine(Path.GetTempPath(), $"secrets-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, """{ "Mail": { "Password": "committed-by-mistake" } }""");
        Environment.SetEnvironmentVariable("SECRETTEST_Metrics__Token", "from-the-environment");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(file)
                .AddEnvironmentVariables("SECRETTEST_")
                .Build();

            var all = new SecretInventory(configuration).All();

            var mail = all.Single(one => one.Key == "Mail:Password");
            var metrics = all.Single(one => one.Key == "Metrics:Token");
            var database = all.Single(one => one.Key == "ConnectionStrings:Default");

            Assert.Equal(SourceKind.SettingsFile, mail.Source);
            Assert.True(mail.NeedsAttention);

            Assert.Equal(SourceKind.Environment, metrics.Source);
            Assert.False(metrics.NeedsAttention);

            Assert.False(database.IsSet);

            // Nothing a reference carries is, or contains, the value.
            foreach (var one in all)
            {
                Assert.DoesNotContain("committed-by-mistake", one.ToString());
                Assert.DoesNotContain("from-the-environment", one.ToString());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("SECRETTEST_Metrics__Token", null);
            File.Delete(file);
        }
    }

    private async Task<string?> StampAsync(Guid account)
    {
        string? stamp = null;

        await factory.InScopeAsync(async services =>
        {
            stamp = (await services.GetRequiredService<AppDbContext>().Users.AsNoTracking()
                .SingleAsync(one => one.Id == account)).SecurityStamp;
        });

        return stamp;
    }

    private static async Task PostAsync(HttpClient browser, string path, string handler)
    {
        var page = await browser.GetAsync(path);

        var fields = HtmlForm.Fill(
            await page.Content.ReadAsStringAsync(),
            new Dictionary<string, string> { ["_handler"] = handler });

        await browser.PostAsync(path, new FormUrlEncodedContent(fields));
    }

    private async Task<Guid> AccountAsync(string email) =>
        (await factory.CreateAccountAsync(email, Password, email)).Id;

    private async Task<Guid> IdOfAsync(string email)
    {
        Guid id = default;

        await factory.InScopeAsync(async services =>
        {
            id = (await services.GetRequiredService<UserManager<ApplicationUser>>()
                .FindByEmailAsync(email))!.Id;
        });

        return id;
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
