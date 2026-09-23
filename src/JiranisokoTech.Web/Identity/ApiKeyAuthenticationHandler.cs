using System.Security.Claims;
using System.Text.Encodings.Web;
using JiranisokoTech.Application.Api;
using JiranisokoTech.Application.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace JiranisokoTech.Web.Identity;

/// <summary>
/// Signing a request in as a key rather than as a person.
/// </summary>
/// <remarks>
/// A second authentication scheme alongside the cookie, not a replacement. The
/// API endpoints ask for this one by name; every page keeps the cookie. That
/// separation is what stops an API key being usable to open a screen and a
/// stolen cookie being usable against the API.
///
/// What it produces is an ordinary ClaimsPrincipal carrying the key's scopes as
/// permission claims — the same claims a signed-in person carries. Every
/// authorization policy in the system then applies to API requests without
/// knowing anything about keys, which is the only way to be sure the two agree.
/// </remarks>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiKeyService keys) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>
    /// The scheme's name.
    /// </summary>
    /// <remarks>
    /// Called SchemeName rather than Scheme because the base handler already
    /// has a Scheme property, and a constant of the same name hides it —
    /// which compiles, warns, and would one day have somebody reading
    /// this.Scheme expecting the framework's value and getting a string
    /// literal.
    /// </remarks>
    public const string SchemeName = "ApiKey";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var header))
        {
            // No result rather than a failure. A request with no credentials at
            // all is anonymous, and the authorization layer decides what that
            // means for the endpoint being asked for.
            return AuthenticateResult.NoResult();
        }

        var value = header.ToString();

        if (!value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.Fail("Send the key as: Authorization: Bearer jts_live_…");
        }

        var secret = value["Bearer ".Length..].Trim();

        if (await keys.ResolveAsync(secret, Context.RequestAborted) is not { } key)
        {
            // Said the same way whether the key never existed or was mistyped.
            Logger.LogWarning("An API request presented a key that matches nothing.");

            return AuthenticateResult.Fail("That key is not recognised.");
        }

        if (!key.IsLive)
        {
            // Told apart from an unknown key on purpose: somebody whose
            // integration stops working needs to know it was turned off rather
            // than wonder whether they have the wrong value.
            Logger.LogWarning("An API request presented the revoked key {Name}.", key.Name);

            return AuthenticateResult.Fail("That key has been revoked.");
        }

        await keys.NoteUseAsync(key, Context.RequestAborted);

        var identity = new ClaimsIdentity(SchemeName);

        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, key.Id.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Name, key.Name));

        // The marker that says this principal is a machine. Anything that must
        // never be done by an integration can check for it, and the audit trail
        // records a key's name rather than inventing a person.
        identity.AddClaim(new Claim("key", key.Id.ToString()));

        foreach (var scope in key.Scopes)
        {
            identity.AddClaim(new Claim(PermissionClaim.Type, scope));
        }

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    /// <summary>
    /// A 401 with a header, not a redirect to the sign-in page.
    /// </summary>
    /// <remarks>
    /// The default challenge for a cookie scheme is a redirect, and a machine
    /// receiving 302 to an HTML login form reports "the API returned a web
    /// page" — which is exactly what it did.
    /// </remarks>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";

        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;

        return Task.CompletedTask;
    }
}
