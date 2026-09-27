using JiranisokoTech.Application.People;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Tests.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.People;

/// <summary>
/// Closing a leaver's sign-in closes it.
/// </summary>
/// <remarks>
/// <b>These exist because it did not.</b> The leavers page showed "Sign-in: Still live" in red
/// until somebody pressed the button, and "Closed" in green afterwards — and the button recorded
/// a time and nothing else. The account went on signing in. That is the one item on the
/// offboarding checklist whose omission is a security finding, and the screen said it was done.
///
/// Through the real application rather than a bare database, because the account is Identity's
/// and the rule that refuses withdrawing the last owner lives there.
/// </remarks>
public class OffboardingAccessTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    [Fact]
    public async Task Closing_a_leavers_sign_in_withdraws_their_account()
    {
        var (leaver, account, closer) = await ALeaverWithAnAccountAsync(role: null);

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<PeopleService>()
                .AccessRemovedAsync(leaver, closer);
        }

        using var after = factory.Services.CreateScope();
        var database = after.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = await after.ServiceProvider
            .GetRequiredService<UserManager<ApplicationUser>>()
            .FindByIdAsync(account.ToString());

        Assert.False(user!.IsActive);

        var offboarding = await database.Offboardings
            .AsNoTracking()
            .SingleAsync(one => one.EmployeeId == leaver);

        Assert.NotNull(offboarding.AccessRemovedAt);
    }

    /// <summary>
    /// When the account cannot be withdrawn, the item stays open.
    /// </summary>
    /// <remarks>
    /// The only owner cannot be withdrawn, because nobody would be left to administer the
    /// system. The page must then go on saying "Still live" — recording the tick anyway would
    /// put the old fault back for exactly the account that matters most.
    /// </remarks>
    [Fact]
    public async Task When_the_account_cannot_be_withdrawn_the_item_stays_open()
    {
        var (leaver, account, closer) = await ALeaverWithAnAccountAsync(role: Roles.Owner);

        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            // Only this one owner, so that the rule has something to refuse.
            foreach (var other in await users.GetUsersInRoleAsync(Roles.Owner))
            {
                if (other.Id != account)
                {
                    await users.RemoveFromRoleAsync(other, Roles.Owner);
                }
            }

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                scope.ServiceProvider.GetRequiredService<PeopleService>()
                    .AccessRemovedAsync(leaver, closer));
        }

        using var after = factory.Services.CreateScope();

        var user = await after.ServiceProvider
            .GetRequiredService<UserManager<ApplicationUser>>()
            .FindByIdAsync(account.ToString());

        Assert.True(user!.IsActive);

        var offboarding = await after.ServiceProvider.GetRequiredService<AppDbContext>()
            .Offboardings
            .AsNoTracking()
            .SingleAsync(one => one.EmployeeId == leaver);

        Assert.Null(offboarding.AccessRemovedAt);
    }

    /// <summary>
    /// Somebody leaving with an account here, and somebody else to close it.
    /// </summary>
    private async Task<(Guid Leaver, Guid Account, Guid Closer)> ALeaverWithAnAccountAsync(
        string? role)
    {
        var suffix = Guid.CreateVersion7().ToString("N")[^8..];
        var user = await factory.CreateAccountAsync(
            $"leaver-{suffix}@jiranisokotech.co.ke", "a-long-enough-password", "Leaving " + suffix);

        using var scope = factory.Services.CreateScope();

        if (role is not null)
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            await users.AddToRoleAsync((await users.FindByIdAsync(user.Id.ToString()))!, role);
        }

        var people = scope.ServiceProvider.GetRequiredService<PeopleService>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var leaver = await people.HireAsync("Leaving " + suffix, today.AddDays(-400));
        var closer = await people.HireAsync("Closing " + suffix, today.AddDays(-400));

        await people.StartAsync(leaver.Id);
        await people.StartAsync(closer.Id);
        await people.LinkAccountAsync(leaver.Id, user.Id);
        await people.RecordLeavingAsync(leaver.Id, today, "Moving abroad");

        return (leaver.Id, user.Id, closer.Id);
    }
}
