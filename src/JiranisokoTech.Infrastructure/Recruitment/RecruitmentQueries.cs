using JiranisokoTech.Domain.Recruitment;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Recruitment;

/// <summary>The reads the hiring screens do.</summary>
public sealed class RecruitmentQueries(AppDbContext database)
{
    public async Task<List<RequisitionRow>> RequisitionsAsync(
        bool openOnly = false, CancellationToken cancellationToken = default)
    {
        var query = database.Requisitions.AsNoTracking();

        if (openOnly)
        {
            query = query.Where(requisition =>
                requisition.Status != RequisitionStatus.Closed
                && requisition.Status != RequisitionStatus.Refused);
        }

        var rows = await query
            // Waiting on somebody first, because that is the list anybody opens
            // this page to work through.
            .OrderBy(requisition => requisition.Status == RequisitionStatus.AwaitingApproval ? 0 : 1)
            .ThenBy(requisition => requisition.JobTitle)
            .Select(requisition => new
            {
                requisition.Id,
                requisition.JobTitle,
                requisition.DepartmentId,
                requisition.Headcount,
                requisition.HiredCount,
                requisition.Status,
                requisition.Justification,
                requisition.Outcome,
                requisition.RaisedById,
            })
            .ToListAsync(cancellationToken);

        var departments = await database.Departments
            .AsNoTracking()
            .ToDictionaryAsync(one => one.Id, one => one.Name, cancellationToken);

        var people = await database.Employees
            .AsNoTracking()
            .ToDictionaryAsync(one => one.Id, one => one.FullName, cancellationToken);

        var adverts = await database.Postings
            .AsNoTracking()
            .GroupBy(posting => posting.RequisitionId)
            .Select(group => new
            {
                RequisitionId = group.Key,
                Open = group.Count(posting => posting.Status == PostingStatus.Published),
                Total = group.Count(),
            })
            .ToDictionaryAsync(row => row.RequisitionId, row => row, cancellationToken);

        return rows.Select(row =>
        {
            var counts = adverts.GetValueOrDefault(row.Id);

            return new RequisitionRow(
                row.Id,
                row.JobTitle,
                row.DepartmentId is { } department
                    ? departments.GetValueOrDefault(department)
                    : null,
                row.Headcount,
                row.HiredCount,
                row.Status,
                row.Justification,
                row.Outcome,
                people.GetValueOrDefault(row.RaisedById) ?? "Somebody who has gone",
                counts?.Open ?? 0,
                counts?.Total ?? 0);
        }).ToList();
    }

    public async Task<RequisitionRow?> RequisitionAsync(
        Guid id, CancellationToken cancellationToken = default) =>
        (await RequisitionsAsync(cancellationToken: cancellationToken))
        .FirstOrDefault(row => row.Id == id);

    public Task<List<PostingRow>> PostingsForAsync(
        Guid requisitionId, CancellationToken cancellationToken = default) =>
        database.Postings
            .AsNoTracking()
            .Where(posting => posting.RequisitionId == requisitionId)
            .OrderBy(posting => posting.Title)
            .Select(posting => new PostingRow(
                posting.Id,
                posting.Title,
                posting.Slug,
                posting.Status,
                posting.Location,
                posting.PublishedAt,
                database.Applications.Count(application => application.PostingId == posting.Id)))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Applications against one advert.
    /// </summary>
    /// <remarks>
    /// The rejection reason is left out of this projection on purpose. It is an
    /// internal note about a person, this list is the thing most likely to be
    /// on a screen in a room with other people in it, and nothing here needs it
    /// to render a row.
    /// </remarks>
    public Task<List<ApplicationRow>> ApplicationsForAsync(
        Guid postingId, CancellationToken cancellationToken = default) =>
        database.Applications
            .AsNoTracking()
            .Where(application => application.PostingId == postingId)
            .Join(
                database.Candidates.AsNoTracking(),
                application => application.CandidateId,
                candidate => candidate.Id,
                (application, candidate) => new ApplicationRow(
                    application.Id,
                    candidate.FullName,
                    candidate.Email,
                    application.Status,
                    application.AppliedAt))
            .OrderBy(row => row.Status)
            .ThenBy(row => row.AppliedAt)
            .ToListAsync(cancellationToken);
}

public sealed record RequisitionRow(
    Guid Id,
    string JobTitle,
    string? DepartmentName,
    int Headcount,
    int HiredCount,
    RequisitionStatus Status,
    string Justification,
    string? Outcome,
    string RaisedByName,
    int OpenAdverts,
    int TotalAdverts)
{
    public int Remaining => Math.Max(0, Headcount - HiredCount);

    /// <summary>The count as somebody would say it.</summary>
    public string Filled => $"{HiredCount} of {Headcount}";
}

public sealed record PostingRow(
    Guid Id,
    string Title,
    string Slug,
    PostingStatus Status,
    string? Location,
    DateTimeOffset? PublishedAt,
    int Applications);

public sealed record ApplicationRow(
    Guid Id,
    string CandidateName,
    string CandidateEmail,
    ApplicationStatus Status,
    DateTimeOffset AppliedAt);
