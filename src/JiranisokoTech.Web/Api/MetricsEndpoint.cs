using System.Security.Cryptography;
using System.Text;
using JiranisokoTech.Infrastructure.Observability;

namespace JiranisokoTech.Web.Api;

/// <summary>
/// Where a scraper reads the counters.
/// </summary>
/// <remarks>
/// Anonymous, like the webhook endpoint, and for the same reason: a metrics collector has
/// no account and cannot be given one. And guarded the same way — a shared secret,
/// compared in constant time, with nothing served to anybody who does not have it.
///
/// <b>The guard is not optional.</b> Metrics are commonly left open on the reasoning that
/// counters are harmless, and they are not: the counts here say how many people sign in,
/// how many invoices go out, when the firm is busy and when nobody is watching. That is a
/// description of the business, available to anybody who finds the address. So with no
/// token configured the endpoint is not mounted at all, rather than mounted open.
/// </remarks>
public static class MetricsEndpoint
{
    public static IEndpointRouteBuilder MapMetrics(this IEndpointRouteBuilder endpoints)
    {
        var token = endpoints.ServiceProvider
            .GetRequiredService<IConfiguration>()["Metrics:Token"];

        if (string.IsNullOrWhiteSpace(token))
        {
            /*
             * Not mounted rather than mounted and refusing. An endpoint that exists and
             * always answers 401 tells a prober that metrics are here and a token is worth
             * looking for; one that is not routed tells them nothing.
             */
            return endpoints;
        }

        endpoints.MapGet("/metrics", (HttpContext context, MetricsReader reader) =>
        {
            if (!Presented(context, token))
            {
                return Results.NotFound();
            }

            /*
             * The scrape format's own content type, which is what makes a collector parse
             * it rather than store it as a document. The version suffix is part of the
             * specification.
             */
            return Results.Text(
                reader.Scrape(),
                "text/plain; version=0.0.4",
                Encoding.UTF8);
        })
        .AllowAnonymous()
        .ExcludeFromDescription();

        return endpoints;
    }

    /// <summary>
    /// Was the right token presented?
    /// </summary>
    /// <remarks>
    /// Accepted from a bearer header or a query string, because collectors differ and the
    /// firm should not have to change collector to fit this. Compared in constant time for
    /// the same reason a webhook signature is: an ordinary comparison returns as soon as
    /// two bytes differ, and how long it took leaks how much of the token was right.
    ///
    /// Answers 404 rather than 401 when it fails, so that a wrong token and no endpoint
    /// look identical from outside.
    /// </remarks>
    private static bool Presented(HttpContext context, string token)
    {
        var offered = context.Request.Headers.Authorization.ToString() is { Length: > 7 } header
            && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? header["Bearer ".Length..].Trim()
                : context.Request.Query["token"].ToString();

        return !string.IsNullOrEmpty(offered)
            && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(offered), Encoding.UTF8.GetBytes(token));
    }
}
