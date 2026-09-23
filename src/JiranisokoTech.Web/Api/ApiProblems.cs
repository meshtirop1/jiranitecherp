namespace JiranisokoTech.Web.Api;

/// <summary>
/// Makes sure an API caller always gets a body, and therefore the status it was sent.
/// </summary>
/// <remarks>
/// This exists because of a fault that appeared twice, in two unrelated features, from the
/// same cause — and the second time is why it is fixed centrally rather than again.
///
/// <see cref="Microsoft.AspNetCore.Builder.StatusCodePagesExtensions"/> re-executes any
/// failing response that has no body of its own, against /not-found, keeping the request's
/// method. On a POST that replay reaches a Blazor endpoint which requires an antiforgery
/// token the caller has never heard of, so the replay is refused — and the original status
/// is replaced by 400.
///
/// For the webhook endpoints that turned a 503 into a 400, which matters because a provider
/// retries a 5xx and gives up permanently on a 4xx. The fix there was to give each refusal
/// a body. That worked, and it only covered the responses this system writes itself.
///
/// A 403 from the authorization middleware has no body and is written before any handler
/// runs, so a key lacking a scope produced 400 — telling an integration its JSON was wrong
/// when the truth was that its key could read and not write. Nothing in a handler could
/// have fixed that.
///
/// So: for the API and webhook paths only, an empty failing response is given a small JSON
/// body on the way out. Status code pages then sees a body and leaves it alone, and the
/// caller gets the status that was actually chosen.
///
/// <b>Scoped by path rather than by branching the pipeline.</b> UseWhen was tried for this
/// and is worse than it looks: the branch it builds changed how authorization picks an
/// authentication scheme, and the public API began redirecting unauthenticated callers to
/// the sign-in page.
/// </remarks>
public static class ApiProblems
{
    public static IApplicationBuilder UseApiProblems(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (!IsMachineFacing(context.Request.Path))
            {
                await next(context);
                return;
            }

            await next(context);

            /*
             * Only when nothing has been written. A handler that answered with its own body
             * has already said something more useful than this could, and overwriting it
             * would replace a specific refusal with a generic one.
             */
            if (context.Response.HasStarted
                || context.Response.StatusCode < 400
                || context.Response.ContentLength is not null
                || !string.IsNullOrEmpty(context.Response.ContentType))
            {
                return;
            }

            context.Response.ContentType = "application/json; charset=utf-8";

            await context.Response.WriteAsJsonAsync(new
            {
                error = Said(context.Response.StatusCode),
                status = context.Response.StatusCode,
            });
        });

    /// <summary>
    /// Is this a path whose callers are programs rather than browsers?
    /// </summary>
    /// <remarks>
    /// Named rather than inlined, because the two lists have to stay the same: a path that
    /// answers JSON must be a path that gets a JSON error, and one added to the API without
    /// being added here would go back to answering 400 for everything.
    /// </remarks>
    private static bool IsMachineFacing(PathString path) =>
        path.StartsWithSegments("/api") || path.StartsWithSegments("/webhooks");

    /// <summary>
    /// What to say, in the few cases that reach here.
    /// </summary>
    /// <remarks>
    /// Deliberately vague about authentication and authorization. A caller that presented no
    /// key and one that presented a key without the right scope are told apart by the status
    /// — 401 against 403 — and not by a sentence naming which permission was missing. Naming
    /// it would tell an unauthorised caller what to go and look for.
    /// </remarks>
    private static string Said(int status) => status switch
    {
        401 => "No usable key was presented.",
        403 => "That key may not do this.",
        404 => "There is nothing at that address.",
        405 => "That address does not accept this method.",
        429 => "Too many requests. Slow down and try again.",
        >= 500 => "Something went wrong here. Try again.",
        _ => "That request could not be answered.",
    };
}
