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
             *   object-src 'none'  — there is no plugin content here and there
             *     never will be.
             *
             * What is NOT here is script-src, and that is the honest part. The
             * page carries two inline scripts that are not ours to move: Blazor
             * renders its import map inline, and the framework needs it to
             * resolve modules at all. A script-src without a nonce on that tag
             * breaks the application outright; a script-src with 'unsafe-inline'
             * would allow every injected script on the page and read, to
             * anybody scanning the headers, as though scripts were restricted.
             * A policy that looks like protection and is not is worse than an
             * absent one, so it is absent and said out loud. Giving the import
             * map a nonce is the way to close it.
             *
             * One surprise worth writing down: every Razor component endpoint
             * comes back with a SECOND Content-Security-Policy header saying
             * frame-ancestors 'self', added by the framework rather than by
             * anything here. Two policies are not a conflict — a browser
             * enforces all of them, so the effective rule is the intersection,
             * and 'none' is narrower than 'self'. The stricter value wins and
             * the duplicate is harmless. It is mentioned because finding two
             * headers and assuming one had overwritten the other is the
             * obvious wrong conclusion.
             */
            headers.ContentSecurityPolicy =
                "default-src 'self'; " +
                "img-src 'self' data:; " +
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
