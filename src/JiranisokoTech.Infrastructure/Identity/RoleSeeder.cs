using JiranisokoTech.Application.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Infrastructure.Identity;

/// <summary>
/// Puts the declared matrix into the database, and keeps it there.
/// </summary>
/// <remarks>
/// Run at every startup, not once at install. A permission added in code
/// changes what the application checks, but a role keeps whatever it was
/// created with — so without this, adding a permission to the matrix grants it
/// to nobody, and the feature refuses its own users by name. That exact fault
/// has bitten the Laravel system this replaces, and the fix there was a button
/// somebody had to remember to press.
///
/// It syncs rather than appends: a permission removed from the matrix is
/// removed from the role. The matrix is the truth, and the database is a copy
/// of it.
/// </remarks>
public sealed class RoleSeeder(
    RoleManager<ApplicationRole> roles,
    ILogger<RoleSeeder> logger)
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        foreach (var roleName in Roles.All)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var role = await roles.FindByNameAsync(roleName);

            if (role is null)
            {
                role = new ApplicationRole(roleName);
                var created = await roles.CreateAsync(role);

                if (!created.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Could not create the role '{roleName}': "
                        + string.Join("; ", created.Errors.Select(e => e.Description)));
                }

                logger.LogInformation("Created role {Role}.", roleName);
            }

            await SyncPermissionsAsync(role, Roles.PermissionsFor(roleName));
        }
    }

    private async Task SyncPermissionsAsync(ApplicationRole role, IReadOnlyList<string> declared)
    {
        var existing = (await roles.GetClaimsAsync(role))
            .Where(claim => claim.Type == PermissionClaim.Type)
            .ToList();

        var held = existing.Select(claim => claim.Value).ToHashSet();
        var wanted = declared.ToHashSet();

        foreach (var permission in wanted.Except(held))
        {
            await roles.AddClaimAsync(role, new System.Security.Claims.Claim(
                PermissionClaim.Type, permission));

            logger.LogInformation("Granted {Permission} to {Role}.", permission, role.Name);
        }

        // Taken out of the matrix means taken away here. A role that keeps a
        // permission the code no longer declares is a permission nobody can
        // find the source of.
        foreach (var claim in existing.Where(claim => !wanted.Contains(claim.Value)))
        {
            await roles.RemoveClaimAsync(role, claim);

            logger.LogInformation("Revoked {Permission} from {Role}.", claim.Value, role.Name);
        }
    }
}
