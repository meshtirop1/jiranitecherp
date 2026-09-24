using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Application.Platform;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Domain.Clients;
using JiranisokoTech.Domain.Money;
using JiranisokoTech.Infrastructure.Business;
using JiranisokoTech.Infrastructure.Recruitment;
using JiranisokoTech.Infrastructure.Work;
using JiranisokoTech.Web.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JiranisokoTech.Web.Api;

/// <summary>
/// What another system may read from this one.
/// </summary>
/// <remarks>
/// Read-only, and that is a decision rather than a stage. Writing through an
/// API means every rule this system enforces has to be enforced twice, or the
/// API has to go through the same services the screens use and answer the same
/// refusals in a shape a machine can act on. Neither is hard; both are work
/// that should be done when something actually needs to write, against that
/// thing's requirements, rather than guessed at now.
///
/// Versioned in the path from the first endpoint. /api/v1 costs nothing today
/// and is the only thing that makes it possible to change a response later
/// without breaking whatever is already reading it — and the caller is on
/// somebody else's server, on somebody else's release schedule.
///
/// Every endpoint sits behind the same permission its screen does, checked by
/// the same policies, because the key's scopes arrive as the same claims a
/// person's role does. An API that had its own idea of who may see what would
/// eventually disagree with the screens, and the direction it disagreed in
/// would not be discovered by anybody using the screens.
/// </remarks>
public static class PublicApi
{
    public static IEndpointRouteBuilder MapPublicApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1")
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName,
            })
            .RequireRateLimiting(ApiLimits.Policy);

        api.MapGet("/whoami", (HttpContext context) => Results.Ok(new
        {
            key = context.User.Identity?.Name,
            scopes = context.User.FindAll(PermissionClaim.Type).Select(claim => claim.Value),
        }))
        .WithSummary("The key being used and what it may do.")
        .WithDescription(
            "Worth calling first when setting an integration up: it answers whether the key "
            + "works and what it can reach, which is otherwise found out one 403 at a time.");

        api.MapGet("/clients", async (
            BusinessQueries queries,
            [FromQuery] ClientStatus? status,
            [FromQuery] int? skip,
            [FromQuery] int? take,
            CancellationToken cancellationToken) =>
        {
            var clients = await queries.ClientsAsync(status, cancellationToken);

            return Results.Ok(Paging.Wrap(clients, Paging.From(skip, take)).Map(client => new
            {
                id = client.Id,
                name = client.Name,
                code = client.Code,
                status = client.Status.ToString(),
                paymentTermDays = client.PaymentTermDays,
                projects = client.Projects,
                owed = client.Owed?.ToString(),
            }));
        })
        .RequirePermission(Permissions.ClientsView)
        .WithSummary("The firm's clients.");

        api.MapGet("/projects", async (
            WorkQueries queries,
            [FromQuery] int? skip,
            [FromQuery] int? take,
            CancellationToken cancellationToken) =>
        {
            var projects = await queries.ProjectsAsync(cancellationToken: cancellationToken);

            return Results.Ok(Paging.Wrap(projects, Paging.From(skip, take)).Map(project => new
            {
                id = project.Id,
                name = project.Name,
                code = project.Code,
                status = project.Status.ToString(),
                dueOn = project.DueOn,
                openItems = project.OpenItems,
                totalItems = project.TotalItems,
            }));
        })
        .RequirePermission(Permissions.ProjectsViewAll)
        .WithSummary("Projects and how much work is left on each.");

        api.MapGet("/invoices", async (
            BusinessQueries queries,
            [FromQuery] InvoiceStatus? status,
            [FromQuery] int? skip,
            [FromQuery] int? take,
            CancellationToken cancellationToken) =>
        {
            var paging = Paging.From(skip, take);

            /*
             * The page is asked for in SQL, not taken out of a list that was read whole.
             * This endpoint used to read every invoice the firm has ever issued, with its
             * lines and its payments, and then hand back fifty — which the scale check
             * measured at 1.8 seconds and forty thousand aggregates for a request whose
             * answer was fifty rows.
             */
            var invoices = await queries.InvoicesAsync(
                status: status,
                skip: paging.Skip,
                take: paging.Take,
                cancellationToken: cancellationToken);

            var total = await queries.CountInvoicesAsync(
                status: status, cancellationToken: cancellationToken);

            return Results.Ok(Paging.Of(invoices, total, paging).Map(invoice => new
            {
                id = invoice.Id,
                number = invoice.Number,
                client = invoice.ClientName,
                status = invoice.Status.ToString(),
                issuedOn = invoice.IssuedOn,
                dueOn = invoice.DueOn,
                total = invoice.Total.ToString(),
                paid = invoice.Paid.ToString(),
                outstanding = invoice.Outstanding.ToString(),
            }));
        })
        .RequirePermission(Permissions.InvoicesView)
        .WithSummary("Invoices, with what is still outstanding on each.");

        /*
         * The openings, which is the one endpoint with an obvious caller: the
         * public website, so that a job posted here appears there without
         * anybody copying it across. It still needs a key — the adverts are
         * public on the careers page, but an unauthenticated API endpoint is a
         * thing to be scraped and rate-limited by address rather than by
         * caller.
         */
        api.MapGet("/openings", async (
            RecruitmentQueries queries,
            [FromQuery] int? skip,
            [FromQuery] int? take,
            CancellationToken cancellationToken) =>
        {
            var openings = await queries.OpeningsAsync(cancellationToken);

            return Results.Ok(Paging.Wrap(openings, Paging.From(skip, take)).Map(opening => new
            {
                slug = opening.Slug,
                title = opening.Title,
                summary = opening.Summary,
                location = opening.Location,
                url = $"/careers/{opening.Slug}",
            }));
        })
        .RequirePermission(Permissions.PostingsManage)
        .WithSummary("Jobs currently advertised.");

        /*
         * Section 68, and the one endpoint here that an application calls on a schedule rather
         * than a person calling from a script. It is what makes feature flags a feature rather
         * than a register: a list of flags nothing can read is a spreadsheet with a schema.
         *
         * A flat map of key to boolean, deliberately. Whatever asks for this wants to look a key
         * up and get a yes or a no; giving it descriptions, histories and identifiers as well
         * would make the common case parse a structure to reach one field, and would put the
         * firm's internal notes into an application's memory.
         *
         * Unpaged, which is the other departure from everything above it. A page of flags is
         * useless — an application needs all of them or none, because a partial answer means it
         * cannot tell "off" from "not in this page". Forty flags is a small object; four
         * thousand would be a different problem and the firm would know about it long before.
         */
        api.MapGet("/flags", async (
            FlagService flags,
            [FromQuery] string? environment,
            CancellationToken cancellationToken) =>
        {
            var where = Deployment.Classify(environment ?? "production");

            return Results.Ok(new
            {
                environment = where.ToString().ToLowerInvariant(),
                flags = await flags.ForAsync(where, cancellationToken),
            });
        })
        .RequirePermission(Permissions.PlatformView)
        .WithSummary("Which feature flags are on, for one environment.")
        .WithDescription(
            "Ask for this periodically and cache the answer. Turning a flag off in the ERP "
            + "does not turn it off in a running application until the application next asks, "
            + "which is the honest consequence of there being no SDK here. An environment the "
            + "firm does not use classifies as other, which is off for everything.");

        return endpoints;
    }

    /// <summary>
    /// The same permission policy the matching screen uses.
    /// </summary>
    /// <remarks>
    /// A small helper so that each endpoint above reads as one line saying what
    /// it needs. The string it builds is the identical policy name the pages
    /// use, which is the point: there is one definition of who may see clients,
    /// and both the screen and the API ask it.
    /// </remarks>
    internal static RouteHandlerBuilder RequirePermission(
        this RouteHandlerBuilder builder, string permission) =>
        builder.RequireAuthorization(new AuthorizeAttribute
        {
            Policy = PermissionClaim.PolicyPrefix + permission,
            AuthenticationSchemes = ApiKeyAuthenticationHandler.SchemeName,
        });
}
