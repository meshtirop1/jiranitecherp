using Microsoft.AspNetCore.Mvc;

namespace JiranisokoTech.Web.Identity;

/// <summary>
/// The endpoints that end a session.
/// </summary>
public static class AuthenticationEndpoints
{
    public static IEndpointRouteBuilder MapAuthenticationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        /*
         * A POST, never a GET.
         *
         * A sign-out on a GET is a URL, and a URL is something a page can put in
         * an <img src>. Anybody could then sign a colleague out by sending them
         * a link — harmless on its own, tiresome in practice, and the same shape
         * of mistake that makes destructive actions reachable by link.
         *
         * Taking the return path from the form body is also what makes the
         * framework demand an antiforgery token here: form binding turns the
         * check on, so this endpoint only answers our own pages.
         */
        endpoints.MapPost("/sign-out", async (
            SignInService signIn,
            [FromForm] string? returnUrl) =>
        {
            await signIn.SignOutAsync();

            return Results.LocalRedirect(Safe(returnUrl));
        });

        return endpoints;
    }

    /// <summary>
    /// Where to land afterwards, restricted to this application.
    /// </summary>
    /// <remarks>
    /// <c>LocalRedirect</c> would throw on anything else, which is a 500 where a
    /// sensible landing page is wanted. Checked here so the answer is always a
    /// page rather than an error.
    /// </remarks>
    private static string Safe(string? returnUrl) =>
        string.IsNullOrWhiteSpace(returnUrl)
        || !returnUrl.StartsWith('/')
        || returnUrl.StartsWith("//", StringComparison.Ordinal)
        || returnUrl.StartsWith("/\\", StringComparison.Ordinal)
            ? "/sign-in"
            : returnUrl;
}
