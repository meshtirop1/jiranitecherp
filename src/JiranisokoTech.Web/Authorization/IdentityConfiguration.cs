using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;

namespace JiranisokoTech.Web.Authorization;

/// <summary>
/// Accounts, sign-in, and who may do what.
/// </summary>
/// <remarks>
/// In the web layer rather than in Infrastructure, and not by accident. The
/// stores are persistence — rows in tables — but cookies, lockout windows and
/// the moment a stale permission is noticed are all properties of a request.
/// Infrastructure has no ASP.NET reference, which is the layering saying the
/// same thing.
/// </remarks>
public static class IdentityConfiguration
{
    public static IServiceCollection AddApplicationIdentity(this IServiceCollection services)
    {
        services.AddIdentity<ApplicationUser, ApplicationRole>(options =>
            {
                /*
                 * Length over composition. Requiring a symbol and a digit
                 * produces Password1! on every account in the building; length
                 * is what actually costs an attacker anything. Twelve is the
                 * floor, and the rest is left to the person.
                 */
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireNonAlphanumeric = false;

                options.User.RequireUniqueEmail = true;
                options.SignIn.RequireConfirmedEmail = true;

                /*
                 * Lockout is per account and temporary. Five attempts is
                 * generous for a person and useless for a script, and fifteen
                 * minutes makes guessing pointless without handing anybody a
                 * way to lock a colleague out all afternoon.
                 */
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.Lockout.AllowedForNewUsers = true;
            })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders();

        /*
         * Permissions travel in the cookie, so a role change does not reach an
         * open session until the principal is rebuilt. Five minutes is the
         * window in which somebody keeps an access they have just lost — short
         * enough to be acceptable, long enough not to hit the database on every
         * request. Revoking something urgent should also deactivate the
         * account, which ends the session immediately.
         */
        services.Configure<SecurityStampValidatorOptions>(options =>
            options.ValidationInterval = TimeSpan.FromMinutes(5));

        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
            options.LoginPath = "/sign-in";
            options.AccessDeniedPath = "/denied";
        });

        return services;
    }

    /// <summary>
    /// Authorization, with a policy built for any declared permission on demand.
    /// </summary>
    public static IServiceCollection AddPermissionAuthorization(this IServiceCollection services)
    {
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

        /*
         * Nothing is readable without signing in unless a page says otherwise.
         * The opposite default — open unless protected — fails silently: a page
         * added without an attribute is a page anybody can read, and nobody
         * finds out until it matters.
         */
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());

        return services;
    }
}
