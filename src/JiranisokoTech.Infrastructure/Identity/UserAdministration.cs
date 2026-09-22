using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Infrastructure.Identity;

/// <summary>
/// Opening accounts, granting roles, and closing accounts again.
/// </summary>
/// <remarks>
/// An administrator never sets somebody else's password, here or anywhere.
/// An administrator who types a password knows it, and from that moment the
/// audit trail cannot tell the two of them apart — every action by that person
/// is deniable, which is exactly the property the trail exists to remove. So an
/// account is opened with no password at all, and the person sets their own
/// through a link.
///
/// The link is a standard Identity reset token: signed, time-limited, tied to
/// the account's security stamp, and stored nowhere. Using it once invalidates
/// it, because setting a password rolls the stamp.
/// </remarks>
public sealed class UserAdministration(
    UserManager<ApplicationUser> users,
    RoleManager<ApplicationRole> roles,
    IClock clock)
{
    /// <summary>
    /// Open an account for somebody, with no password and no way in yet.
    /// </summary>
    /// <remarks>
    /// <see cref="ApplicationUser.EmailConfirmed"/> stays false. It becomes true
    /// when they follow the link and set a password, which is the only evidence
    /// this system ever gets that the address reaches them.
    /// </remarks>
    public async Task<ApplicationUser> InviteAsync(
        string email,
        string displayName,
        string? jobTitle = null,
        CancellationToken cancellationToken = default)
    {
        if (await users.FindByEmailAsync(email) is not null)
        {
            throw new InvalidOperationException(
                $"There is already an account for {email}.");
        }

        var user = new ApplicationUser(email, displayName)
        {
            JobTitle = jobTitle,
            InvitedAt = clock.Now,
        };

        var created = await users.CreateAsync(user);

        if (!created.Succeeded)
        {
            throw new InvalidOperationException(
                "Could not open the account: " + Describe(created));
        }

        return user;
    }

    /// <summary>
    /// A fresh link for somebody to set their own password.
    /// </summary>
    /// <remarks>
    /// Generated on demand rather than stored, so there is no table of live
    /// tokens to leak and no expiry to sweep. Asking again simply issues another
    /// one; the old one keeps working until it expires or a password is set,
    /// which is the standard behaviour and not worth fighting.
    ///
    /// Until mail is wired up an administrator passes this on by hand. When it
    /// is, the same link is what gets sent.
    /// </remarks>
    public async Task<string> SignInLinkAsync(Guid userId, string baseAddress)
    {
        var user = await Required(userId);

        var token = await users.GeneratePasswordResetTokenAsync(user);

        return $"{baseAddress.TrimEnd('/')}/set-password"
            + $"?email={Uri.EscapeDataString(user.Email!)}"
            + $"&token={Uri.EscapeDataString(token)}";
    }

    /// <summary>
    /// Set a password from a link, and take the address as confirmed.
    /// </summary>
    /// <remarks>
    /// Confirming the email here is the one piece of evidence available: they
    /// had a link that was sent to that address. It is not proof, and it is not
    /// treated as any stronger than it is — the account still holds only what
    /// its roles allow.
    /// </remarks>
    public async Task SetPasswordAsync(string email, string token, string password)
    {
        var user = await users.FindByEmailAsync(email)
            // Deliberately the same message as a bad token. Telling a stranger
            // which addresses have accounts turns this page into a way of
            // finding out who works here.
            ?? throw new InvalidOperationException(
                "That link is no longer valid. Ask an administrator for another.");

        var result = await users.ResetPasswordAsync(user, token, password);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(Describe(result));
        }

        if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            await users.UpdateAsync(user);
        }
    }

    public async Task GrantAsync(Guid userId, string role)
    {
        var user = await Required(userId);

        if (!await roles.RoleExistsAsync(role))
        {
            throw new InvalidOperationException($"There is no role called '{role}'.");
        }

        if (await users.IsInRoleAsync(user, role))
        {
            return;
        }

        var granted = await users.AddToRoleAsync(user, role);

        if (!granted.Succeeded)
        {
            throw new InvalidOperationException(Describe(granted));
        }
    }

    /// <summary>
    /// Take a role away.
    /// </summary>
    /// <remarks>
    /// With one refusal: the last owner keeps the role. An administrator who
    /// removes it from the only person holding it leaves a system nobody can
    /// administer, recoverable only by somebody with database access — and the
    /// seeder will not help, because it refuses to act once an owner exists.
    /// </remarks>
    public async Task RevokeAsync(Guid userId, string role)
    {
        var user = await Required(userId);

        if (!await users.IsInRoleAsync(user, role))
        {
            return;
        }

        if (role == Roles.Owner && await OwnerCountAsync() <= 1)
        {
            throw new InvalidOperationException(
                $"{user.DisplayName} is the only owner. Give somebody else the role first, or "
                + "there will be nobody who can administer this system.");
        }

        var revoked = await users.RemoveFromRoleAsync(user, role);

        if (!revoked.Succeeded)
        {
            throw new InvalidOperationException(Describe(revoked));
        }
    }

    /// <summary>
    /// Withdraw access, without deleting anything.
    /// </summary>
    /// <remarks>
    /// Two refusals, both about being locked out of your own system. Nobody
    /// deactivates themselves, and the last owner is not deactivated at all.
    ///
    /// Taking effect is not left to the next sign-in: the security stamp is
    /// rolled, so the open session fails its next validation, which is within
    /// five minutes.
    /// </remarks>
    public async Task DeactivateAsync(Guid userId, Guid actingUserId)
    {
        var user = await Required(userId);

        if (userId == actingUserId)
        {
            throw new InvalidOperationException(
                "You cannot withdraw your own access. Ask another administrator.");
        }

        if (await users.IsInRoleAsync(user, Roles.Owner) && await OwnerCountAsync() <= 1)
        {
            throw new InvalidOperationException(
                $"{user.DisplayName} is the only owner, so this would leave nobody able to "
                + "administer the system.");
        }

        if (!user.IsActive)
        {
            return;
        }

        user.IsActive = false;
        await Save(user);

        // Ends the session they are in now rather than at their next attempt.
        await users.UpdateSecurityStampAsync(user);
    }

    public async Task ReactivateAsync(Guid userId)
    {
        var user = await Required(userId);

        if (user.IsActive)
        {
            return;
        }

        user.IsActive = true;
        await Save(user);
    }

    /// <summary>
    /// Clear a lockout early.
    /// </summary>
    /// <remarks>
    /// Lockout expires on its own after fifteen minutes, so this exists for the
    /// person who is locked out and needs to work now. It does not change the
    /// password and does not touch the account otherwise.
    /// </remarks>
    public async Task UnlockAsync(Guid userId)
    {
        var user = await Required(userId);

        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
    }

    public async Task<IList<string>> RolesOfAsync(Guid userId) =>
        await users.GetRolesAsync(await Required(userId));

    private async Task<int> OwnerCountAsync() =>
        (await users.GetUsersInRoleAsync(Roles.Owner)).Count;

    private async Task Save(ApplicationUser user)
    {
        var saved = await users.UpdateAsync(user);

        if (!saved.Succeeded)
        {
            throw new InvalidOperationException(Describe(saved));
        }
    }

    private async Task<ApplicationUser> Required(Guid userId) =>
        await users.FindByIdAsync(userId.ToString())
        ?? throw new InvalidOperationException("There is no account with that identifier.");

    private static string Describe(IdentityResult result) =>
        string.Join("; ", result.Errors.Select(error => error.Description));
}
