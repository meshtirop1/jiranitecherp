using JiranisokoTech.Application.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Infrastructure.Identity;

/// <summary>
/// The first account, so that a new installation can be got into at all.
/// </summary>
/// <remarks>
/// A system with roles, permissions and a sign-in form and no accounts is a
/// locked building with a very good door. Something has to create the first
/// person, and the only two honest ways are a command somebody runs on the
/// host, or configuration read at startup. This is the second, because the
/// deployment is a container and running a command inside it is the awkward
/// path.
///
/// What it deliberately does not do:
///
/// It does not ship a default password. A seeded admin/admin is a backdoor with
/// a friendly name, and every installation that ever had one has it still.
/// Without configuration this does nothing at all and says so in the log.
///
/// It does not touch an account that already exists. Leaving the bootstrap
/// variables set on a running host is the normal, forgetful thing to do, and if
/// this re-applied them every start, then a password the owner had since
/// changed would be silently reset to the one in an old environment file on
/// every deploy.
///
/// It does not run once the firm has an owner. After that, accounts are made by
/// people, with an audit entry naming who made them.
/// </remarks>
public sealed class OwnerSeeder(
    UserManager<ApplicationUser> users,
    IConfiguration configuration,
    ILogger<OwnerSeeder> logger)
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var email = configuration["Bootstrap:OwnerEmail"];
        var password = configuration["Bootstrap:OwnerPassword"];

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            // Said at warning level because on a fresh database it is the
            // reason nobody can sign in, and that is worth finding in a log
            // without being asked to raise the level first.
            logger.LogWarning(
                "No first account configured. Set Bootstrap__OwnerEmail and "
                + "Bootstrap__OwnerPassword to open one; until then nobody can sign in.");

            return;
        }

        if (await users.FindByEmailAsync(email) is not null)
        {
            return;
        }

        var owners = await users.GetUsersInRoleAsync(Roles.Owner);

        if (owners.Count > 0)
        {
            logger.LogInformation(
                "Bootstrap account {Email} not created: this installation already has an owner.",
                email);

            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var owner = new ApplicationUser(email, configuration["Bootstrap:OwnerName"] ?? email)
        {
            // Nobody can send this person a confirmation link yet, and sign-in
            // requires a confirmed address. Confirming the very first account
            // is the one place where doing so is the whole point.
            EmailConfirmed = true,
        };

        var created = await users.CreateAsync(owner, password);

        if (!created.Succeeded)
        {
            /*
             * Loud on purpose. This only runs when the installation has no
             * owner, so a failure here means nobody can get in — and a process
             * that stays up while being unusable is a fault somebody finds days
             * later. The message carries the reasons, not the password.
             */
            throw new InvalidOperationException(
                "Could not create the first account: "
                + string.Join("; ", created.Errors.Select(error => error.Description)));
        }

        var assigned = await users.AddToRoleAsync(owner, Roles.Owner);

        if (!assigned.Succeeded)
        {
            throw new InvalidOperationException(
                $"Created the first account but could not give it the {Roles.Owner} role: "
                + string.Join("; ", assigned.Errors.Select(error => error.Description)));
        }

        logger.LogWarning(
            "Opened the first account for {Email} as {Role}. Change this password, "
            + "then remove the bootstrap settings from the environment.",
            email,
            Roles.Owner);
    }
}
