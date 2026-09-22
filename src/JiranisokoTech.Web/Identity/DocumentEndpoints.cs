using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Application.Documents;
using Microsoft.AspNetCore.Authorization;

namespace JiranisokoTech.Web.Identity;

/// <summary>
/// Handing an attachment back to somebody allowed to read it.
/// </summary>
/// <remarks>
/// Served through code rather than from a folder, for the same reason CVs are:
/// anything under wwwroot goes to whoever guesses its address, and this folder
/// holds client contracts and photographs of people's receipts.
///
/// The address is the attachment's id, never the stored name, so what the file
/// is called on disk does not appear in a browser history, a proxy log, or a
/// referrer header.
/// </remarks>
public static class DocumentEndpoints
{
    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/documents/{id:guid}", async (
            Guid id,
            DocumentService documents,
            IAuthorizationService authorization,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (await documents.FindAsync(id, cancellationToken) is not { } attachment)
            {
                return Results.NotFound();
            }

            /*
             * The permission is the one that governs the thing this is attached
             * to, resolved from the same table the upload uses. A document
             * inherits its owner's confidentiality: a contract attached to a
             * client is as private as the client.
             *
             * Checked here rather than by an attribute on the endpoint, because
             * which permission applies is not known until the row is read.
             */
            var needed = Application.Documents.Documents.PermissionToSee(attachment.Kind);

            var allowed = await authorization.AuthorizeAsync(
                context.User, PermissionClaim.PolicyPrefix + needed);

            if (!allowed.Succeeded)
            {
                // Not found rather than forbidden. A 403 on a guessable
                // identifier confirms that a document exists and who it belongs
                // to, which is most of what an attacker wanted.
                return Results.NotFound();
            }

            var contents = await documents.OpenAsync(attachment, cancellationToken);

            if (contents is null)
            {
                // The row says there is a file and there is not. A 404 rather
                // than a 500: the record is fine, the file is gone.
                return Results.NotFound();
            }

            /*
             * Always an attachment, never inline.
             *
             * A PDF or an SVG rendered in the browser runs whatever it asks the
             * viewer to run, on this application's origin and with this
             * person's session. Downloading hands the file to the operating
             * system, where it belongs.
             */
            return Results.File(
                contents,
                "application/octet-stream",
                attachment.FileName,
                enableRangeProcessing: false);
        })
        .RequireAuthorization()
        .WithName("Document");

        return endpoints;
    }
}
