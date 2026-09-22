using JiranisokoTech.Application.Authorization;
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
                AuthenticationSchemes = ApiKeyAuthenticationHandler.Scheme,
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
            CancellationToken cancellationToken) =>
        {
            var clients = await queries.ClientsAsync(status, cancellationToken);

            return Results.Ok(clients.Select(client => new
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
            WorkQueries queries, CancellationToken cancellationToken) =>
        {
            var projects = await queries.ProjectsAsync(cancellationToken: cancellationToken);

            return Results.Ok(projects.Select(project => new
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
            CancellationToken cancellationToken) =>
        {
            var invoices = await queries.InvoicesAsync(
                status: status, cancellationToken: cancellationToken);

            return Results.Ok(invoices.Select(invoice => new
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
            RecruitmentQueries queries, CancellationToken cancellationToken) =>
        {
            var openings = await queries.OpeningsAsync(cancellationToken);

            return Results.Ok(openings.Select(opening => new
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
    private static RouteHandlerBuilder RequirePermission(
        this RouteHandlerBuilder builder, string permission) =>
        builder.RequireAuthorization(new AuthorizeAttribute
        {
            Policy = PermissionClaim.PolicyPrefix + permission,
            AuthenticationSchemes = ApiKeyAuthenticationHandler.Scheme,
        });
}
