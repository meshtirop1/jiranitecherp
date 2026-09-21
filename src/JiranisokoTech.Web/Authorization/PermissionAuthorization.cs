using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using JiranisokoTech.Application.Authorization;
using Microsoft.Extensions.Options;

namespace JiranisokoTech.Web.Authorization;

/// <summary>A requirement that the caller holds one named permission.</summary>
public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

/// <summary>
/// Grants the requirement when the principal carries the permission as a claim.
/// </summary>
/// <remarks>
/// Permissions reach the principal as claims on the roles the user holds, which
/// means the check is a lookup in a token already in memory rather than a query
/// per page. The cost of that is staleness: a permission taken away mid-session
/// stays in the cookie until it is refreshed. Identity's security stamp
/// validation handles exactly that, and it is configured with a short interval
/// in <c>ServiceCollectionExtensions</c> for this reason.
/// </remarks>
public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        var holds = context.User.HasClaim(
            claim => claim.Type == PermissionClaim.Type
                     && string.Equals(claim.Value, requirement.Permission, StringComparison.Ordinal));

        if (holds)
        {
            context.Succeed(requirement);
        }

        // Not failing explicitly: another handler may satisfy this requirement
        // by a different route. Silence here means "not by me", not "no".
        return Task.CompletedTask;
    }
}

/// <summary>
/// Builds a policy for any permission the first time it is asked for.
/// </summary>
/// <remarks>
/// The alternative is registering thirty-one policies at startup and adding a
/// line every time a permission is declared — two places to change, one of
/// which fails silently when forgotten, because a missing policy throws only
/// when somebody reaches that page.
///
/// Here the policy name carries the permission, so
/// <c>[Authorize("permission:tasks.create")]</c> needs no registration at all,
/// and the set of enforceable permissions is exactly the set that is declared.
/// </remarks>
public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
    : DefaultAuthorizationPolicyProvider(options)
{
    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        // An explicitly registered policy always wins, so a permission can be
        // overridden with something more specific when one ever needs to be.
        var registered = await base.GetPolicyAsync(policyName);

        if (registered is not null)
        {
            return registered;
        }

        if (!policyName.StartsWith(PermissionClaim.PolicyPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var permission = policyName[PermissionClaim.PolicyPrefix.Length..];

        return new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(permission))
            .Build();
    }
}

/// <summary>Reads permissions off a signed-in principal.</summary>
public static class PrincipalExtensions
{
    public static bool HasPermission(this ClaimsPrincipal principal, string permission) =>
        principal.HasClaim(
            claim => claim.Type == PermissionClaim.Type
                     && string.Equals(claim.Value, permission, StringComparison.Ordinal));

    public static IReadOnlySet<string> Permissions(this ClaimsPrincipal principal) =>
        principal.FindAll(PermissionClaim.Type).Select(claim => claim.Value).ToHashSet();
}
