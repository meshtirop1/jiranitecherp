using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Web.Identity;
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

            /*
             * Our own sign-in manager, registered here rather than left to the
             * default. It carries two rules — a deactivated account cannot sign
             * in, and a principal carries its owner's display name — and both
             * have to hold on every route into a session, not only the login
             * form. Registering the subclass is what makes that true for the
             * remembered-cookie path and for anything added later.
             */
            .AddSignInManager<ApplicationSignInManager>()
            .AddDefaultTokenProviders();

        // One place that knows an attempt has to be recorded. Spread across the
        // pages that sign people in, the third one forgets, and the omission is
        // invisible: sign-in still works and only the history is wrong.
        services.AddScoped<SignInService>();
        services.AddScoped<TwoFactor>();

        /*
         * How long a session may go on believing what it was told, and the one
         * number that decides whether revoking anything means anything.
         *
         * Permissions and the account's own state travel in the cookie, so a
         * role change, a withdrawal, a password being set or a security stamp
         * rolled by "sign out everywhere" reaches an open session only when the
         * principal is next checked against the database. This interval is how
         * long that takes, and the framework's default for it is thirty minutes
         * — which makes every revocation in this system a suggestion. Somebody
         * who finds a sign-in they do not recognise, ends every session and sets
         * a new password has, on the default, left whoever it was reading
         * invoices for another half hour.
         *
         * A minute instead. The cost is one read of one row by primary key per
         * signed-in person per minute — per minute and not per request, because
         * a check that passes stamps the cookie with the time it happened, and
         * the next request inside the minute reads that stamp instead of the
         * database. TimeSpan.Zero would make revocation immediate and move the
         * read onto every request of every page; a minute is short enough to
         * say out loud to somebody whose account has been used by a stranger,
         * and cheap enough that nobody has to think about it again.
         *
         * The comments elsewhere that used to name five minutes now point here
         * rather than repeating the figure. Three of them agreed with this line
         * only because nobody had changed it yet, and the first edit would have
         * left them all lying. Where a number still has to be spelled out is in
         * what a page says to a person, because "within the validation interval"
         * is not English.
         */
        services.Configure<SecurityStampValidatorOptions>(options =>
            options.ValidationInterval = TimeSpan.FromMinutes(1));

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
