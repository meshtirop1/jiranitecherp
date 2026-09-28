using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using JiranisokoTech.Domain.Documents;
using JiranisokoTech.Domain.Privacy;
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
        };
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
