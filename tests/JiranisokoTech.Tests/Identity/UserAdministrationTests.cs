using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using SignInService = JiranisokoTech.Web.Identity.SignInService;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Identity;

/// <summary>
/// Opening and closing accounts.
/// </summary>
/// <remarks>
/// The tests worth reading are the refusals. Each one is a way of locking
/// everybody out of their own system, and every one of them is a single click
/// on a page an administrator uses daily.
/// </remarks>
public class UserAdministrationTests(ApplicationFactory factory)
    : IClassFixture<ApplicationFactory>
{
    private const string Password = "a-long-enough-password";

    /// <summary>
    /// An account is opened with no password at all.
    /// </summary>
    /// <remarks>
    /// An administrator who types somebody's password knows it, and from that
    /// moment the audit trail cannot tell the two of them apart — every action
    /// by that person becomes deniable, which is the property the trail exists
    /// to remove.
    /// </remarks>
    [Fact]
    public async Task An_invited_account_has_no_password_and_cannot_sign_in_yet()
    {
        var user = await InviteAsync("invited@jiranisokotech.co.ke", "Precious");

        await InScopeAsync(async services =>
        {
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var stored = await users.FindByIdAsync(user.ToString());

            Assert.False(await users.HasPasswordAsync(stored!));
            Assert.False(stored!.EmailConfirmed);
            Assert.NotNull(stored.InvitedAt);
        });

        Assert.Equal(
            SignInOutcome.Refused,
            await AttemptAsync("invited@jiranisokotech.co.ke", Password));
    }

    [Fact]
    public async Task Two_accounts_cannot_share_an_address()
    {
        await InviteAsync("twice@jiranisokotech.co.ke", "Somebody");

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InviteAsync("twice@jiranisokotech.co.ke", "Somebody Else"));

        Assert.Contains("already an account", refused.Message);
    }

    /// <summary>
    /// The whole point of the invitation: they set their own password, and
    /// following the link is the only evidence this system gets that the
    /// address reaches them.
    /// </summary>
    [Fact]
    public async Task Following_the_link_lets_somebody_set_their_own_password()
    {
        var user = await InviteAsync("setting@jiranisokotech.co.ke", "Duncan");

        await InScopeAsync(async services =>
        {
            var administration = services.GetRequiredService<UserAdministration>();
            var link = await administration.SignInLinkAsync(user, "https://erp.example");

            Assert.Contains("/set-password", link);

            await administration.SetPasswordAsync(
                "setting@jiranisokotech.co.ke", TokenIn(link), Password);
        });

        Assert.Equal(
            SignInOutcome.Succeeded,
            await AttemptAsync("setting@jiranisokotech.co.ke", Password));
    }

    [Fact]
    public async Task A_link_cannot_be_used_twice()
    {
        var user = await InviteAsync("once@jiranisokotech.co.ke", "Purity");

        await InScopeAsync(async services =>
        {
            var administration = services.GetRequiredService<UserAdministration>();
            var link = await administration.SignInLinkAsync(user, "https://erp.example");
            var token = TokenIn(link);

            await administration.SetPasswordAsync("once@jiranisokotech.co.ke", token, Password);

            // Setting a password rolls the security stamp the token was tied to.
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => administration.SetPasswordAsync(
                    "once@jiranisokotech.co.ke", token, "a-different-password"));
        });
    }

    /// <summary>
    /// Telling a stranger which addresses have accounts turns this page into a
    /// way of finding out who works here.
    /// </summary>
    [Fact]
    public async Task An_unknown_address_is_answered_exactly_as_a_bad_token_is()
    {
        var user = await InviteAsync("known-link@jiranisokotech.co.ke", "Charity");

        await InScopeAsync(async services =>
        {
            var administration = services.GetRequiredService<UserAdministration>();
            var link = await administration.SignInLinkAsync(user, "https://erp.example");

            var noSuchAccount = await Assert.ThrowsAsync<InvalidOperationException>(
                () => administration.SetPasswordAsync(
                    "stranger@example.com", TokenIn(link), Password));

            Assert.Contains("no longer valid", noSuchAccount.Message);
        });
    }

    [Fact]
    public async Task A_role_can_be_granted_and_taken_away()
    {
        var user = await InviteAsync("roles@jiranisokotech.co.ke", "Tirop Meshack");

        await InScopeAsync(async services =>
        {
            var administration = services.GetRequiredService<UserAdministration>();

            await administration.GrantAsync(user, Roles.ProjectManager);
            Assert.Contains(Roles.ProjectManager, await administration.RolesOfAsync(user));

            await administration.RevokeAsync(user, Roles.ProjectManager);
            Assert.Empty(await administration.RolesOfAsync(user));
        });
    }

    /// <summary>
    /// Giving somebody a role, and taking it away, is on the audit trail.
    /// </summary>
    /// <remarks>
    /// It was not. A grant is a row in Identity's join table, which the audit capture could not
    /// see, and the only column moving on the account was the security stamp, excluded as a
    /// token — so making somebody an administrator left no trace at all, while the checklist
    /// said every important mutation was audited.
    /// </remarks>
    [Fact]
    public async Task Granting_and_revoking_a_role_is_on_the_audit_trail()
    {
        var user = await InviteAsync("audited-roles@jiranisokotech.co.ke", "Wanjiru Kamau");

        await InScopeAsync(async services =>
        {
            var administration = services.GetRequiredService<UserAdministration>();

            await administration.GrantAsync(user, Roles.ProjectManager);
            await administration.RevokeAsync(user, Roles.ProjectManager);
        });

        await InScopeAsync(async services =>
        {
            var trail = await services.GetRequiredService<AppDbContext>().AuditEntries
                .AsNoTracking()
                .Where(entry => entry.SubjectId == user && entry.Action.StartsWith("role."))
                .ToListAsync();

            var granted = Assert.Single(trail, entry => entry.Action == "role.granted");
            Assert.Equal(Roles.ProjectManager, granted.After!["Role"]);

            var revoked = Assert.Single(trail, entry => entry.Action == "role.revoked");
            Assert.Equal(Roles.ProjectManager, revoked.Before!["Role"]);
        });
    }

    /// <summary>
    /// Switching the second factor off an account is on the audit trail.
    /// </summary>
    /// <remarks>
    /// The flag had been excluded from the trail alongside the password hash, as though it were
    /// a credential. It is a yes or no, and whether somebody turned it off is one of the first
    /// questions asked about an account that has been misused.
    /// </remarks>
    [Fact]
    public async Task Switching_the_second_factor_off_is_on_the_audit_trail()
    {
        var user = await InviteAsync("audited-mfa@jiranisokotech.co.ke", "Otieno Achieng");

        await InScopeAsync(async services =>
        {
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var stored = await users.FindByIdAsync(user.ToString());

            await users.SetTwoFactorEnabledAsync(stored!, true);
            await users.SetTwoFactorEnabledAsync(stored!, false);
        });

        await InScopeAsync(async services =>
        {
            var trail = await services.GetRequiredService<AppDbContext>().AuditEntries
                .AsNoTracking()
                .Where(entry => entry.SubjectId == user)
                .ToListAsync();

            Assert.Contains(trail, entry =>
                entry.Action == "application_user.modified"
                && entry.After is { } after
                && after.TryGetValue(nameof(ApplicationUser.TwoFactorEnabled), out var value)
                && value == "false"
                && entry.Before![nameof(ApplicationUser.TwoFactorEnabled)] == "true");
        });
    }

    [Fact]
    public async Task A_role_that_does_not_exist_is_refused()
    {
        var user = await InviteAsync("norole@jiranisokotech.co.ke", "Somebody");

        await InScopeAsync(async services =>
        {
            var administration = services.GetRequiredService<UserAdministration>();

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => administration.GrantAsync(user, "supreme_overlord"));
        });
    }

    /*
     * The three owner rules below each get an application of their own.
     *
     * "The last owner" is a fact about the whole system, so a test that shares
     * one with its neighbours is really asserting how many owners the tests
     * before it happened to create. One of them passed for that reason and then
     * failed when another test was added above it, which is the failure mode
     * these are supposed to be guarding against.
     */

    /// <summary>
    /// The refusal that matters most.
    /// </summary>
    /// <remarks>
    /// Removing the owner role from the only person holding it leaves a system
    /// nobody can administer, recoverable only with database access — and the
    /// first-account seeder will not help, because it refuses to act once an
    /// owner exists.
    /// </remarks>
    [Fact]
    public async Task The_last_owner_keeps_the_role()
    {
        await using var application = new ApplicationFactory();

        await Alone(application, async (administration, owner) =>
        {
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                () => administration.RevokeAsync(owner, Roles.Owner));

            Assert.Contains("only owner", refused.Message);
            Assert.Contains(Roles.Owner, await administration.RolesOfAsync(owner));
        });
    }

    [Fact]
    public async Task The_last_owner_cannot_be_withdrawn()
    {
        await using var application = new ApplicationFactory();

        await Alone(application, async (administration, owner) =>
        {
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                () => administration.DeactivateAsync(owner, Guid.CreateVersion7()));

            Assert.Contains("only owner", refused.Message);
        });
    }

    [Fact]
    public async Task An_owner_can_be_stood_down_once_there_is_another()
    {
        await using var application = new ApplicationFactory();

        await application.InScopeAsync(async services =>
        {
            var administration = services.GetRequiredService<UserAdministration>();

            var first = await administration.InviteAsync("owner-one@jiranisokotech.co.ke", "Vincent");
            var second = await administration.InviteAsync("owner-two@jiranisokotech.co.ke", "Charity");

            await administration.GrantAsync(first.Id, Roles.Owner);
            await administration.GrantAsync(second.Id, Roles.Owner);

            await administration.RevokeAsync(first.Id, Roles.Owner);

            Assert.DoesNotContain(Roles.Owner, await administration.RolesOfAsync(first.Id));
            Assert.Contains(Roles.Owner, await administration.RolesOfAsync(second.Id));
        });
    }

    /// <summary>One owner in an application of its own, and nobody else.</summary>
    private static Task Alone(
        ApplicationFactory application, Func<UserAdministration, Guid, Task> work) =>
        application.InScopeAsync(async services =>
        {
            var administration = services.GetRequiredService<UserAdministration>();

            var owner = await administration.InviteAsync(
                "sole-owner@jiranisokotech.co.ke", "Vincent Bungei");

            await administration.GrantAsync(owner.Id, Roles.Owner);

            await work(administration, owner.Id);
        });

    [Fact]
    public async Task Somebody_locked_out_can_be_let_back_in()
    {
        var user = await InviteAsync("locked@jiranisokotech.co.ke", "Purity");

        await InScopeAsync(async services =>
        {
            var administration = services.GetRequiredService<UserAdministration>();
            var link = await administration.SignInLinkAsync(user, "https://erp.example");

            await administration.SetPasswordAsync(
                "locked@jiranisokotech.co.ke", TokenIn(link), Password);
        });

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await AttemptAsync("locked@jiranisokotech.co.ke", "not-the-password");
        }

        Assert.Equal(
            SignInOutcome.LockedOut,
            await AttemptAsync("locked@jiranisokotech.co.ke", Password));

        await InScopeAsync(services =>
            services.GetRequiredService<UserAdministration>().UnlockAsync(user));

        Assert.Equal(
            SignInOutcome.Succeeded,
            await AttemptAsync("locked@jiranisokotech.co.ke", Password));
    }

    /// <summary>
    /// The list says what somebody needs to know in one phrase. Four columns of
    /// ticks make an administrator work the state out for themselves every time,
    /// and the one they misread is the withdrawn account that still looks fine.
    /// </summary>
    [Fact]
    public async Task The_directory_says_where_each_account_stands()
    {
        var invited = await InviteAsync("state-invited@jiranisokotech.co.ke", "Never Came");

        await InScopeAsync(async services =>
        {
            var directory = services.GetRequiredService<UserDirectory>();
            var row = await directory.FindAsync(invited);

            Assert.NotNull(row);
            Assert.Equal("Invited, never signed in", row!.State);
            Assert.Empty(row.Roles);
            Assert.Null(row.EmployeeName);
        });
    }

    private static string TokenIn(string link) =>
        Uri.UnescapeDataString(link.Split("&token=")[1]);

    private async Task<Guid> InviteAsync(string email, string displayName)
    {
        Guid id = default;

        await InScopeAsync(async services =>
        {
            var administration = services.GetRequiredService<UserAdministration>();
            var user = await administration.InviteAsync(email, displayName);

            id = user.Id;
        });

        return id;
    }

    private Task<SignInOutcome> AttemptAsync(string email, string password) =>
        factory.InRequestAsync(services =>
            services.GetRequiredService<SignInService>().PasswordSignInAsync(
                email, password, remember: false, ipAddress: null, userAgent: null));

    private Task InScopeAsync(Func<IServiceProvider, Task> work) => factory.InScopeAsync(work);
}
