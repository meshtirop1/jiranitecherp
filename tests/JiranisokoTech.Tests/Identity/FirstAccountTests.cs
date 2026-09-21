using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Web.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Identity;

/// <summary>
/// The account that lets a new installation be got into, and the three things
/// it must refuse to do.
/// </summary>
public class FirstAccountTests
{
    private const string Email = "owner@jiranisokotech.co.ke";
    private const string Password = "the-first-password-here";

    /// <summary>
    /// Without configuration, nothing is created — not a default administrator,
    /// not anything. A seeded admin/admin is a backdoor with a friendly name,
    /// and every installation that ever shipped one has it still.
    /// </summary>
    [Fact]
    public async Task Nothing_is_created_when_no_first_account_is_configured()
    {
        await using var application = new ApplicationFactory();

        // Forces the host to start, so the seeder has actually run before this
        // asserts on what it did.
        using var browser = application.CreateBrowser();
        await browser.GetAsync("/sign-in");

        await application.InScopeAsync(async services =>
        {
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();

            Assert.Empty(await users.GetUsersInRoleAsync(Roles.Owner));
        });
    }

    [Fact]
    public async Task A_configured_first_account_can_sign_in_and_owns_the_place()
    {
        await using var application = new BootstrappedFactory(Email, Password);

        var outcome = await application.InRequestAsync(services =>
            services.GetRequiredService<SignInService>().PasswordSignInAsync(
                Email, Password, remember: false, ipAddress: null, userAgent: null));

        Assert.Equal(SignInOutcome.Succeeded, outcome);

        await application.InScopeAsync(async services =>
        {
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var owner = await users.FindByEmailAsync(Email);

            Assert.NotNull(owner);
            Assert.Contains(Roles.Owner, await users.GetRolesAsync(owner!));
        });
    }

    /// <summary>
    /// A stale bootstrap password cannot re-key the account on a later start.
    /// </summary>
    /// <remarks>
    /// Leaving those variables set on a running host is the normal, forgetful
    /// thing to do. If the seeder re-applied them, a password the owner had
    /// since changed would be silently reset to the one in an old environment
    /// file on every deploy — which is not a bootstrap, it is a permanent
    /// backdoor held by whoever has read access to that file.
    /// </remarks>
    [Fact]
    public async Task An_existing_account_is_never_re_keyed()
    {
        await using var application = new BootstrappedFactory(Email, Password);

        await application.InScopeAsync(async services =>
        {
            var stale = new OwnerSeeder(
                services.GetRequiredService<UserManager<ApplicationUser>>(),
                Settings(Email, "a-completely-different-password"),
                services.GetRequiredService<ILogger<OwnerSeeder>>());

            await stale.SeedAsync();
        });

        Assert.Equal(
            SignInOutcome.Refused,
            await AttemptAsync(application, Email, "a-completely-different-password"));

        Assert.Equal(
            SignInOutcome.Succeeded,
            await AttemptAsync(application, Email, Password));
    }

    /// <summary>
    /// Once the firm has an owner, accounts are opened by people — with an
    /// audit entry naming who opened them.
    /// </summary>
    [Fact]
    public async Task A_second_first_account_is_not_opened()
    {
        await using var application = new BootstrappedFactory(Email, Password);

        await application.InScopeAsync(async services =>
        {
            var later = new OwnerSeeder(
                services.GetRequiredService<UserManager<ApplicationUser>>(),
                Settings("someone.else@jiranisokotech.co.ke", "another-long-password"),
                services.GetRequiredService<ILogger<OwnerSeeder>>());

            await later.SeedAsync();

            var users = services.GetRequiredService<UserManager<ApplicationUser>>();

            Assert.Null(await users.FindByEmailAsync("someone.else@jiranisokotech.co.ke"));
            Assert.Single(await users.GetUsersInRoleAsync(Roles.Owner));
        });
    }

    private static Task<SignInOutcome> AttemptAsync(
        ApplicationFactory application, string email, string password) =>
        application.InRequestAsync(services =>
            services.GetRequiredService<SignInService>().PasswordSignInAsync(
                email, password, remember: false, ipAddress: null, userAgent: null));

    private static IConfiguration Settings(string email, string password) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bootstrap:OwnerEmail"] = email,
                ["Bootstrap:OwnerPassword"] = password,
            })
            .Build();

    /// <summary>The same application, started as a new installation would be.</summary>
    private sealed class BootstrappedFactory(string email, string password) : ApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.UseSetting("Bootstrap:OwnerEmail", email);
            builder.UseSetting("Bootstrap:OwnerPassword", password);
            builder.UseSetting("Bootstrap:OwnerName", "Vincent Bungei");
        }
    }
}
