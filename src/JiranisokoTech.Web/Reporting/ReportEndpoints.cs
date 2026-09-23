using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Infrastructure.Reporting;
using JiranisokoTech.Web.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace JiranisokoTech.Web.Reporting;

/// <summary>
/// The reporting page, as a file.
/// </summary>
/// <remarks>
/// An endpoint rather than a component, because a component renders HTML into a
/// page and this has to arrive as a download with a content type and a filename
/// on it. The same reason the attachments are served this way.
///
/// The system this replaces has five reports and every one of them exports, which
/// is not a coincidence: a figure somebody has to act on gets sent to somebody
/// else, and if the application cannot produce the file then a person retypes the
/// numbers into one — at which point the firm has two sets of figures and the
/// wrong one is in the email.
/// </remarks>
public static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/reports/where-things-stand.csv", async (
            ReportingQueries reporting,
            IClock clock,
            IAuthorizationService authorization,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var today = clock.Today;
            var state = await reporting.StateAsync(today, cancellationToken);

            /*
             * The two money sections are asked for separately, with the same
             * permissions the page puts its own money sections behind.
             *
             * reports.view alone is not permission to see what clients owe. A
             * department head holds reporting so they can see their team's hours
             * and absence, and the page deliberately keeps the debt and the
             * unbilled work behind invoices.view — so a file that carried them
             * would hand a head exactly what the screen refuses them, in a form
             * they can forward. The page and the file ask the same authorization
             * service for the same policy, so the two cannot drift.
             */
            var money = await Holds(authorization, context, Permissions.InvoicesView);
            var owed = await Holds(authorization, context, Permissions.ExpensesViewAll);

            return Results.File(
                Csv.Bytes(StandingCsv.For(state, today, money, owed)),
                "text/csv; charset=utf-8",
                StandingCsv.FileName(today),
                enableRangeProcessing: false);
        })
        /*
         * Behind the permission the page is behind, and on the cookie rather
         * than the API key: this is a browser download, and the API key scheme
         * is named only by the endpoints under /api. A key issued to read the
         * client list is not a key to the firm's reporting.
         */
        .RequireAuthorization(PermissionClaim.PolicyPrefix + Permissions.ReportsView)
        .WithName("StandingCsv");

        return endpoints;
    }

    private static async Task<bool> Holds(
        IAuthorizationService authorization, HttpContext context, string permission) =>
        (await authorization.AuthorizeAsync(
            context.User, PermissionClaim.PolicyPrefix + permission)).Succeeded;
}
