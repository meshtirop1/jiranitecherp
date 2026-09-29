using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using JiranisokoTech.Domain.Documents;
using JiranisokoTech.Domain.Privacy;
using JiranisokoTech.Domain.Support;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Privacy;

/// <summary>
/// Everything the firm holds about one person, as a single file they can be given.
/// </summary>
/// <remarks>
/// Section 55's data export, and what the Data Protection Act's right of access actually needs:
/// until this, answering an access request meant somebody opening a dozen screens and copying
/// what they found into a letter, which is slow, and is how something gets left out.
///
/// <b>Whole records, not chosen fields.</b> Each table that refers to the person is read and
/// its rows are written out as they are, so a column added to a record next year is in the
/// export without anybody remembering to add it here. The one thing removed is the list of
/// unpublished domain events every entity carries, which is machinery rather than data.
///
/// <b>Documents are listed, not included.</b> The file names, kinds and dates are in the export;
/// the files themselves are downloaded from the record as usual, because a personnel file can
/// run to tens of megabytes and a JSON document is the wrong container for it.
///
/// Every export is written to the audit trail by the endpoint that serves it, because an export
/// of somebody's whole record is the most sensitive read this system offers.
/// </remarks>
public sealed class SubjectAccessExport(AppDbContext database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,

        // The file is for a person to read. The default encoder escapes anything that could be
        // unsafe inside HTML, so a phone number arrived as "\u002B254…" and an apostrophe in a
        // name as "\u0027". It is served only as a download and never placed in a page.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                info =>
                {
                    foreach (var property in info.Properties.Where(one => one.Name == "events").ToList())
                    {
                        info.Properties.Remove(property);
                    }
                },
            },
        },
    };

    /// <summary>The export for a request, or null when the request names nobody on file.</summary>
    public async Task<byte[]?> ForAsync(PrivacyRequest request, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        if (request.SubjectId is not { } id)
        {
            return null;
        }

        object? held = request.SubjectKind switch
        {
            SubjectKind.Employee => await EmployeeAsync(id, cancellationToken),
            SubjectKind.Candidate => await CandidateAsync(id, cancellationToken),
            _ => null,
        };

        if (held is null)
        {
            return null;
        }

        return JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                Reference = request.Reference,
                ExportedAt = at,
                Subject = request.Subject,
                Held = held,
                AuditTrail = await TrailAsync(id, cancellationToken),
            },
            Json);
    }

    private async Task<object?> EmployeeAsync(Guid id, CancellationToken cancellationToken)
    {
        var employee = await database.Employees.AsNoTracking()
            .FirstOrDefaultAsync(one => one.Id == id, cancellationToken);

        if (employee is null)
        {
            return null;
        }

        var account = employee.AccountId is { } accountId
            ? await database.Users.AsNoTracking()
                .Where(user => user.Id == accountId)
                .Select(user => new
                {
                    user.Email,
                    user.DisplayName,
                    user.PhoneNumber,
                    user.IsActive,
                    user.CreatedAt,
                    user.LastSignedInAt,
                    user.TwoFactorEnabled,
                })
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        var signIns = employee.AccountId is { } signedInAs
            ? await database.Set<Identity.SignInRecord>().AsNoTracking()
                .Where(record => record.UserId == signedInAs)
                .OrderBy(record => record.At)
                .ToListAsync(cancellationToken)
            : [];

        var cycles = await database.ReviewCycles.AsNoTracking().ToListAsync(cancellationToken);

        return new
        {
            StaffRecord = employee,
            Account = account,
            SignIns = signIns,
            Leave = await database.Leave.AsNoTracking()
                .Where(one => one.EmployeeId == id).ToListAsync(cancellationToken),
            Time = await database.TimeEntries.AsNoTracking()
                .Where(one => one.EmployeeId == id).ToListAsync(cancellationToken),
            Expenses = await database.Expenses.AsNoTracking()
                .Where(one => one.EmployeeId == id).ToListAsync(cancellationToken),
            Payslips = await database.Payslips.AsNoTracking()
                .Where(one => one.EmployeeId == id).ToListAsync(cancellationToken),
            Equipment = await database.Assets.AsNoTracking()
                .Where(asset => asset.Movements.Any(move => move.PersonId == id))
                .ToListAsync(cancellationToken),
            Goals = await database.Goals.AsNoTracking()
                .Where(goal => goal.ForEmployeeId == id).ToListAsync(cancellationToken),
            Reviews = cycles.SelectMany(cycle => cycle.Reviews.Where(review => review.EmployeeId == id)),
            Onboarding = await database.Onboardings.AsNoTracking()
                .Where(one => one.EmployeeId == id).ToListAsync(cancellationToken),
            Offboarding = await database.Offboardings.AsNoTracking()
                .Where(one => one.EmployeeId == id).ToListAsync(cancellationToken),
            Documents = await DocumentsAsync([AttachedTo.Employee, AttachedTo.Photo], id, cancellationToken),
            SupportRequests = await SupportAsync(id, cancellationToken),
        };
    }

    /// <summary>
    /// The help desk tickets this person raised, and what the firm said back to them.
    /// </summary>
    /// <remarks>
    /// Section 26 arrived after this export and would have been silently missing from it: a
    /// ticket carries somebody's name, their words and a promise made to them, which is exactly
    /// the shape of thing section 55 exists to hand over. Nothing failed, because an export
    /// cannot fail for leaving something out.
    ///
    /// <b>Only the half of the thread that was said to them.</b> An internal note is a
    /// colleague's working remark about the request — "their own script is doing this, do not
    /// say so yet" — and the whole of section 26 is arranged so that it never reaches the person
    /// who asked. Putting it in here would make the subject access response the one route by
    /// which it does, which is the opposite of what this file is for. That is a decision rather
    /// than an oversight, and if the firm decides the other way the change is one line:
    /// <c>Audience.Requester</c> becomes every audience.
    ///
    /// The files come back by ticket rather than by person, because an attachment's owner is the
    /// ticket it hangs off and not the requester — so DocumentsAsync, which matches an owner
    /// against one identifier, cannot find them.
    /// </remarks>
    private async Task<object> SupportAsync(Guid id, CancellationToken cancellationToken)
    {
        var tickets = await database.Tickets.AsNoTracking()
            .Where(one => one.From == Requester.Colleague && one.RequesterId == id)
            .OrderBy(one => one.Number)
            .ToListAsync(cancellationToken);

        if (tickets.Count == 0)
        {
            return Array.Empty<object>();
        }

        var raised = tickets.Select(one => one.Id).ToList();

        var files = await database.Attachments.AsNoTracking()
            .Where(one => one.Kind == AttachedTo.Ticket && raised.Contains(one.OwnerId))
            .Select(one => new { one.OwnerId, one.FileName, one.UploadedAt, one.SizeBytes })
            .ToListAsync(cancellationToken);

        return tickets.Select(one => new
        {
            one.Reference,
            one.Subject,
            one.Priority,
            one.Status,
            one.RaisedAt,
            one.RespondBy,
            one.FirstRespondedAt,
            one.ResolvedAt,
            Conversation = one.AsTheySeeIt.Select(said => new { said.At, said.Text }),
            Documents = files
                .Where(file => file.OwnerId == one.Id)
                .Select(file => new { file.FileName, file.UploadedAt, file.SizeBytes }),
        });
    }

    private async Task<object?> CandidateAsync(Guid id, CancellationToken cancellationToken)
    {
        var candidate = await database.Candidates.AsNoTracking()
            .FirstOrDefaultAsync(one => one.Id == id, cancellationToken);

        if (candidate is null)
        {
            return null;
        }

        var applications = await database.Applications.AsNoTracking()
            .Where(one => one.CandidateId == id).ToListAsync(cancellationToken);

        var applied = applications.Select(one => one.Id).ToList();

        return new
        {
            Candidate = candidate,
            Applications = applications,
            Interviews = await database.Interviews.AsNoTracking()
                .Where(one => applied.Contains(one.ApplicationId)).ToListAsync(cancellationToken),
            Assessments = await database.Assessments.AsNoTracking()
                .Where(one => applied.Contains(one.ApplicationId)).ToListAsync(cancellationToken),
            Offers = await database.Offers.AsNoTracking()
                .Where(one => applied.Contains(one.ApplicationId)).ToListAsync(cancellationToken),
        };
    }

    private async Task<object> DocumentsAsync(
        AttachedTo[] kinds, Guid owner, CancellationToken cancellationToken) =>
        await database.Attachments.AsNoTracking()
            .Where(one => kinds.Contains(one.Kind) && one.OwnerId == owner)
            .Select(one => new { one.FileName, one.Kind, one.UploadedAt, one.SizeBytes })
            .ToListAsync(cancellationToken);

    /// <summary>What the trail records about them, in order.</summary>
    private async Task<object> TrailAsync(Guid id, CancellationToken cancellationToken) =>
        (await database.AuditEntries.AsNoTracking()
            .Where(entry => entry.SubjectId == id)
            .ToListAsync(cancellationToken))
        .OrderBy(entry => entry.OccurredAt)
        .Select(entry => new
        {
            entry.OccurredAt,
            entry.Action,
            entry.ActorName,
            Changes = entry.Changes().ToDictionary(
                change => change.Key, change => new { change.Value.From, change.Value.To }),
            entry.Reason,
        });
}
