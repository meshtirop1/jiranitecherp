using System.Security.Claims;
using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Application.Documents;
using JiranisokoTech.Domain.Documents;
using JiranisokoTech.Infrastructure.People;
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
            PeopleQueries people,
            IAuthorizationService authorization,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (await documents.FindAsync(id, cancellationToken) is not { } attachment)
            {
                return Results.NotFound();
            }

            // Checked here rather than by an attribute on the endpoint, because
            // which permission applies is not known until the row is read.
            if (!await MayReadAsync(
                attachment, people, authorization, context, cancellationToken))
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

    /// <summary>
    /// Whether this caller may be handed this particular file.
    /// </summary>
    /// <remarks>
    /// For every kind but one this is the table lookup and nothing else. A
    /// document inherits the confidentiality of the thing it hangs off, so a
    /// contract attached to a client is exactly as private as the client, and
    /// the upload and the download read the same table so the two cannot
    /// disagree.
    ///
    /// A personnel file is the case where that inheritance gives the wrong
    /// answer, and gives it quietly. The table maps Employee to employees.view,
    /// which is permission to read the staff roster — names, job titles, who
    /// answers to whom — and it is held by HR, every department head, every
    /// delivery manager, the administrator and the owner. That is the right
    /// audience for a roster and the wrong one for what people actually attach
    /// to a person: a signed contract with a salary on it, a disciplinary
    /// letter, a scan of a passport. Inheriting employees.view would mean a
    /// delivery manager who holds it only so they can see who is on the team
    /// could also read the pay and the disciplinary history of everybody on it,
    /// and nobody ever reports having been handed a file they should not have
    /// had.
    /// </remarks>
    private static async Task<bool> MayReadAsync(
        Attachment attachment,
        PeopleQueries people,
        IAuthorizationService authorization,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (attachment.Kind == AttachedTo.Employee)
        {
            return await MayReadAPersonnelFileAsync(
                attachment, people, authorization, context, cancellationToken);
        }

        var needed = Application.Documents.Documents.PermissionToSee(attachment.Kind);

        var allowed = await authorization.AuthorizeAsync(
            context.User, PermissionClaim.PolicyPrefix + needed);

        return allowed.Succeeded;
    }

    /// <summary>
    /// The people whose job the file is, and the person it is about.
    /// </summary>
    /// <remarks>
    /// employees.manage rather than employees.view, so the audience is HR, the
    /// administrator and the owner rather than half the firm. That is a
    /// narrowing of what the table alone would allow and never a widening:
    /// every role holding manage holds view as well, so nobody gains a file by
    /// this rule who would not have been given it anyway.
    ///
    /// The other half — being the person the file is about — cannot be written
    /// as a permission at all, which is why it lives here and not in the table.
    /// Permissions answer "may this kind of person do this kind of thing"; this
    /// is the separate question of whether they may do it to this particular
    /// record, and the answer depends on a row. Putting it in the table would
    /// mean inventing a permission per employee.
    ///
    /// Two gaps are left open deliberately, because closing either needs a
    /// column that does not exist. Everything attached to a person sits in one
    /// bucket, so a training certificate a department head has every reason to
    /// see is locked away as tightly as a disciplinary letter — the strictest
    /// rule has to cover the lot. And the person sees a letter the instant it is
    /// attached, which is a poor way to learn you are being disciplined; HR
    /// attaching one before the conversation has happened will be read on the
    /// employee's own screen first. Both want a per-document confidentiality
    /// flag on the attachment, set when it is uploaded. Until that exists this
    /// is the narrower of the two honest choices.
    /// </remarks>
    private static async Task<bool> MayReadAPersonnelFileAsync(
        Attachment attachment,
        PeopleQueries people,
        IAuthorizationService authorization,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var managed = await authorization.AuthorizeAsync(
            context.User, PermissionClaim.PolicyPrefix + Permissions.EmployeesManage);

        if (managed.Succeeded)
        {
            return true;
        }

        /*
         * Both sides have to be a real person.
         *
         * An account with no staff record resolves to nobody, and so does an API
         * key, whose name claim carries the key's own identifier rather than an
         * account's. Neither may come out of this as the person the file is
         * about. The refusal is written out rather than left to a lifted
         * comparison happening to be false, because the work item page made the
         * same mistake in the shape where it does not: it compared two nullable
         * identifiers, null matched null, and every engineer could read every
         * unassigned card. Whoever makes an attachment's owner nullable one day
         * should not be able to reintroduce that by accident here.
         */
        if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var account))
        {
            return false;
        }

        return await people.EmployeeForAccountAsync(account, cancellationToken) is { } viewer
            && viewer == attachment.OwnerId;
    }
}
