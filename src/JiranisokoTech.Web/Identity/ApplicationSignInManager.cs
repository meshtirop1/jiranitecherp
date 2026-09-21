using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using JiranisokoTech.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JiranisokoTech.Web.Identity;

/// <summary>
/// Sign-in, with two additions the framework does not know about.
/// </summary>
/// <remarks>
/// Both are here rather than on the sign-in page on purpose. A check that lives
/// on one page protects that page; a check here protects every route into a
/// session — the login form, a remembered cookie being refreshed, a
/// two-factor completion, and whatever is added later by somebody who has never
/// read this file.
/// </remarks>
public sealed class ApplicationSignInManager(
    UserManager<ApplicationUser> userManager,
    IHttpContextAccessor contextAccessor,
    IUserClaimsPrincipalFactory<ApplicationUser> claimsFactory,
    IOptions<IdentityOptions> optionsAccessor,
    ILogger<SignInManager<ApplicationUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<ApplicationUser> confirmation)
    : SignInManager<ApplicationUser>(
        userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
    /// <summary>
    /// A deactivated account cannot sign in, by any route.
    /// </summary>
    /// <remarks>
    /// Identity's own lockout is a temporary consequence of getting a password
    /// wrong. This is a decision somebody made — a leaver, a suspension — and
    /// the difference matters: lockout expires on its own, and this does not.
    ///
    /// Because it is enforced here, an existing session is also ended the next
    /// time the principal is validated, which is within the five minute window
    /// the security stamp is checked on. Deactivating somebody is therefore a
    /// single action with a bounded effect, rather than a flag that only
    /// applies at the next login they might never attempt.
    /// </remarks>
    public override async Task<bool> CanSignInAsync(ApplicationUser user)
    {
        if (!user.IsActive)
        {
            Logger.LogInformation(
                "Refused sign-in for deactivated account {UserId}.", user.Id);

            return false;
        }

        return await base.CanSignInAsync(user);
    }

    /// <summary>
    /// Add the display name to the principal, so the audit trail can name the
    /// actor without a query on every save.
    /// </summary>
    public override async Task<ClaimsPrincipal> CreateUserPrincipalAsync(ApplicationUser user)
    {
        var principal = await base.CreateUserPrincipalAsync(user);

        if (principal.Identity is ClaimsIdentity identity
            && !string.IsNullOrWhiteSpace(user.DisplayName))
        {
            identity.AddClaim(new Claim("display_name", user.DisplayName));
        }

        return principal;
    }
}
