namespace JiranisokoTech.Web;

/// <summary>
/// The headers that tell a browser what it may do with this application.
/// </summary>
/// <remarks>
/// Every one of these is a defence the browser applies on our behalf, and every
/// one is absent by default. The system this replaces sets them; this one set
/// only HSTS until now, which is a regression rather than a decision.
///
/// They are added to every response including static assets and the API,
/// because the response that matters is whichever one an attacker can get a
/// browser to treat as a document — and that is not always the one anybody
/// expected.
/// </remarks>
public static class SecurityHeaders
{
    public static void UseSecurityHeaders(this WebApplication app) =>
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;

            /*
             * A content security policy, and a deliberately partial one.
             *
             * What is here is enforceable today and worth having:
             *
             *   frame-ancestors 'none'  — nothing may put this application in a
             *     frame, which is what stops a clickjacking page from
             *     overlaying an invisible copy of the approvals screen under a
             *     button somebody thinks does something else.
             *
             *   base-uri 'self'  — an injected <base> tag would otherwise
             *     re-point every relative URL on the page, including the form
             *     actions, at somebody else's host.
             *
             *   form-action 'self'  — a form cannot be made to post elsewhere,
             *     which is the difference between an injected form being ugly
             *     and it being a credential harvester.
             *
             *   connect-src 'self'  — a script that does run cannot post what
             *     it found to somewhere else.
             *
             *   object-src 'none'  — there is no plugin content here and there
             *     never will be.
             *
             * What is NOT here is script-src, and the reason is worth the
             * space, because the first version of this got it wrong.
             *
             * Blazor renders its import map inline and needs it to resolve
             * modules at all. A script-src without a nonce on that tag breaks
             * the application; a script-src with 'unsafe-inline' permits every
             * injected script on the page while reading, to anybody scanning
             * the headers, as though scripts were restricted — protection that
             * looks like protection and is not, which is worse than none.
             *
             * So script-src is absent on purpose. The mistake was also setting
             * default-src 'self', which sounds unrelated and is not:
             * default-src is the fallback for every fetch directive that has
             * not been named, script-src included. The browser therefore
             * applied 'self' to scripts anyway and blocked the import map,
             * reporting it as a default-src violation. Nothing visible broke,
             * because nothing on this application is interactive yet — which is
             * exactly how a policy like this reaches production.
             *
             * The directives below are therefore named one at a time. Nothing
             * falls back to anything, and adding script-src is a deliberate act
             * with a nonce attached rather than a side effect of tightening
             * something else.
             */
            headers.ContentSecurityPolicy =
                "img-src 'self' data:; " +
                "font-src 'self'; " +
                "connect-src 'self'; " +
                "object-src 'none'; " +
                "base-uri 'self'; " +
                "form-action 'self'; " +
                "frame-ancestors 'none'";

            // Stops a browser deciding for itself that the text file somebody
            // uploaded is really JavaScript. Attachments are served as
            // application/octet-stream precisely so they are never executed,
            // and that only holds if the browser believes the header.
            headers["X-Content-Type-Options"] = "nosniff";

            // frame-ancestors above covers the browsers that read a CSP; this
            // covers the ones that do not. Both, because the cost is a header.
            headers["X-Frame-Options"] = "DENY";

            // The full address of an internal page — which carries invoice and
            // person identifiers in its path — must not travel to another host
            // in a Referer header. The origin alone is enough for anybody who
            // legitimately wants to know where a visitor came from.
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

            await next();
        });
}
