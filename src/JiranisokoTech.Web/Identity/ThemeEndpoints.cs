namespace JiranisokoTech.Web.Identity;

/// <summary>
/// Remembering whether somebody wants this light or dark.
/// </summary>
/// <remarks>
/// A cookie set by a form post, with no JavaScript anywhere in it. These pages are
/// statically rendered, and the usual approach — a script that reads localStorage and
/// stamps the document — would mean the page arriving in the wrong theme and repainting a
/// moment later, on every single navigation. A cookie is read on the server before the
/// first byte is written, so the page is never the wrong colour at all.
///
/// Three states rather than a boolean: light, dark, and nothing said. The third is the
/// default and means "whatever this device prefers", which is what the media query
/// answers. A boolean would have to pick one of the two as the default and would override
/// the reader's own operating system setting on their behalf.
/// </remarks>
public static class ThemeEndpoints
{
    public const string CookieName = "theme";

    public static IEndpointRouteBuilder MapThemeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/theme", (HttpContext context) =>
        {
            var theme = context.Request.Form["theme"].ToString();

            /*
             * Only the three values this system understands, and anything else clears the
             * preference. The value is written into an attribute on the html element, so an
             * unvalidated one would be an attribute injection — and the fix is not escaping,
             * it is refusing to store anything that is not one of three known words.
             */
            if (theme is "light" or "dark")
            {
                context.Response.Cookies.Append(CookieName, theme, new CookieOptions
                {
                    // A year, because a theme is a preference and not a session. Somebody
                    // who has to set it again every Monday will stop setting it.
                    Expires = DateTimeOffset.UtcNow.AddYears(1),

                    // Not HttpOnly-sensitive in the way a session is, and still marked so:
                    // nothing in the browser needs to read it, and the narrower setting
                    // costs nothing.
                    HttpOnly = true,
                    IsEssential = true,
                    SameSite = SameSiteMode.Lax,
                    Secure = context.Request.IsHttps,
                    Path = "/",
                });
            }
            else
            {
                context.Response.Cookies.Delete(CookieName);
            }

            /*
             * Back where they were, and only if it is somewhere here. A redirect to whatever
             * a form field contained would be an open redirect — the classic phishing
             * primitive, and one that a theme switch is a very odd place to acquire.
             */
            var back = context.Request.Form["back"].ToString();

            return Results.LocalRedirect(
                string.IsNullOrWhiteSpace(back) || !back.StartsWith('/') ? "/" : back);
        })
        .ExcludeFromDescription();

        return endpoints;
    }

    /// <summary>What this request should be painted as, or nothing.</summary>
    /// <remarks>
    /// Read on the server so the attribute is on the html element in the first byte sent.
    /// Anything other than the two known words is treated as nothing said, which covers a
    /// cookie somebody edited by hand.
    /// </remarks>
    public static string? ChosenTheme(this HttpContext? context) =>
        context?.Request.Cookies[CookieName] is "light" or "dark"
            ? context.Request.Cookies[CookieName]
            : null;
}
