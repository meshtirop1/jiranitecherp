using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Application.Recruitment;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Web.Identity;

/// <summary>
/// Handing a CV back to somebody who is allowed to read it.
/// </summary>
public static class CvEndpoints
{
    public static IEndpointRouteBuilder MapCvEndpoints(this IEndpointRouteBuilder endpoints)
    {
        /*
         * Served through code rather than from a folder.
         *
         * These files are strangers' CVs. Anything under wwwroot is handed to
         * whoever guesses its address, and a directory of applicants' documents
         * published to the internet is the kind of mistake that ends up in a
         * newspaper. Every download passes the permission check first.
         *
         * The address is the application id, not the stored filename, so the
         * name on disk never appears in a browser history or a proxy log.
         */
        endpoints.MapGet("/cv/{applicationId:guid}", async (
            Guid applicationId,
            AppDbContext database,
            ICvStore cvs,
            CancellationToken cancellationToken) =>
        {
            var application = await database.Applications
                .AsNoTracking()
                .FirstOrDefaultAsync(one => one.Id == applicationId, cancellationToken);

            if (application?.CvStoredName is not { } stored)
            {
                return Results.NotFound();
            }

            var contents = await cvs.OpenAsync(stored, cancellationToken);

            if (contents is null)
            {
                // The row says there is a file and there is not. Worth a 404
                // rather than a 500: the application is fine, the file is gone.
                return Results.NotFound();
            }

            /*
             * Always an attachment, never inline.
             *
             * A PDF rendered in the browser runs whatever the PDF asks the
             * viewer to run, on this application's origin. Downloading it hands
             * the file to the operating system, where it belongs.
             */
            return Results.File(
                contents,
                "application/octet-stream",
                application.CvFileName ?? "cv",
                enableRangeProcessing: false);
        })
        .RequireAuthorization(PermissionClaim.PolicyPrefix + Permissions.CandidatesView);

        return endpoints;
    }
}
