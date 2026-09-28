using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Application.Privacy;
using JiranisokoTech.Domain.Privacy;
using JiranisokoTech.Infrastructure.Privacy;

namespace JiranisokoTech.Web.Identity;

/// <summary>
/// The file that answers a subject access request.
/// </summary>
public static class PrivacyEndpoints
{
    public static IEndpointRouteBuilder MapPrivacyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        /*
         * A GET, and a file rather than a page. It is behind privacy.respond, the permission that
         * answers these requests, and it is refused for an erasure request: handing somebody's
         * whole record to whoever is processing its deletion is not what they asked for.
         *
         * Every export is written to the audit trail before the file is sent. It is the most
         * sensitive read in the system — one download is everything held about a person — and
         * "who exported my record, and when" has to have an answer.
         */
        endpoints.MapGet("/privacy/requests/{id:guid}/export", async (
            Guid id,
            PrivacyService privacy,
            SubjectAccessExport export,
            IAccessLog accessLog,
            IClock clock,
            CancellationToken cancellationToken) =>
        {
            if (await privacy.OneAsync(id, cancellationToken) is not { } request
                || request.Ask != PrivacyAsk.Access)
            {
                return Results.NotFound();
            }

            if (await export.ForAsync(request, clock.Now, cancellationToken) is not { } file)
            {
                return Results.NotFound();
            }

            await accessLog.ViewedAsync(
                "privacy_request.exported", nameof(PrivacyRequest), request.Id, cancellationToken);

            return Results.File(
                file, "application/json", $"subject-access-{request.Reference}.json");
        })
        .RequireAuthorization(PermissionClaim.PolicyPrefix + Permissions.PrivacyRespond);

        return endpoints;
    }
}
