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

    /// <summary>
    /// The adverts a stranger may read.
    /// </summary>
    /// <remarks>
    /// Published only, and it returns nothing else — no requisition, no
    /// headcount, no justification. Those are an internal argument for spending
    /// money, and the projection is the place to make sure none of it can reach
    /// a public page by accident.
    ///
    /// An advert past its closing date drops off on its own, so nobody has to
    /// remember to take it down.
    /// </remarks>
    public async Task<List<OpeningRow>> OpeningsAsync(
        CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        return await database.Postings
            .AsNoTracking()
            .Where(posting => posting.Status == PostingStatus.Published)
            .Where(posting => posting.ClosesOn == null || posting.ClosesOn >= today)
            .OrderByDescending(posting => posting.PublishedAt)
            .Select(posting => new OpeningRow(
                posting.Id,
                posting.Title,
                posting.Slug,
                posting.Summary,
                posting.Description,
                posting.Location))
            .ToListAsync(cancellationToken);
    }

    /// <summary>One advert, by the address it answers at.</summary>
    public async Task<OpeningRow?> OpeningAsync(
        string slug, CancellationToken cancellationToken = default) =>
        (await OpeningsAsync(cancellationToken))
        .FirstOrDefault(opening => opening.Slug == slug);

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

    /// <summary>Every advert, whatever state it is in.</summary>
    public Task<List<PostingRow>> PostingsAsync(CancellationToken cancellationToken = default) =>
        database.Postings
            .AsNoTracking()
            .OrderByDescending(posting => posting.PublishedAt)
            .ThenBy(posting => posting.Title)
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
    /// Applications across every advert, for the people who work through them.
    /// </summary>
    /// <remarks>
    /// The existing read takes one advert, which suits a page about that advert
    /// and not the question somebody in HR actually has, which is "what has come
    /// in". Ordered oldest first: an application nobody has looked at for three
    /// weeks is the one that matters, and it is the one a newest-first list
    /// hides.
    /// </remarks>
    public async Task<List<WaitingApplicationRow>> ApplicationsAsync(
        ApplicationStatus? status = null,
        Guid? postingId = null,
        CancellationToken cancellationToken = default)
    {
        var query = database.Applications.AsNoTracking();

        if (status is { } only)
        {
            query = query.Where(application => application.Status == only);
        }

        if (postingId is { } advert)
        {
            query = query.Where(application => application.PostingId == advert);
        }

        var applications = await query
            .OrderBy(application => application.AppliedAt)
            .Select(application => new
            {
                application.Id,
                application.PostingId,
                application.CandidateId,
                application.Status,
                application.AppliedAt,
                HasCv = application.CvStoredName != null,
            })
            .ToListAsync(cancellationToken);

        var candidates = await database.Candidates
            .AsNoTracking()
            .ToDictionaryAsync(one => one.Id, one => new { one.FullName, one.Email }, cancellationToken);

        var postings = await database.Postings
            .AsNoTracking()
            .ToDictionaryAsync(one => one.Id, one => one.Title, cancellationToken);

        return [.. applications.Select(application => new WaitingApplicationRow(
            application.Id,
            application.CandidateId,
            candidates.GetValueOrDefault(application.CandidateId)?.FullName ?? "A candidate since removed",
            candidates.GetValueOrDefault(application.CandidateId)?.Email ?? string.Empty,
            postings.GetValueOrDefault(application.PostingId) ?? "An advert since removed",
            application.PostingId,
            application.Status,
            application.AppliedAt,
            application.HasCv))];
    }

    /// <summary>
    /// One application, with the candidate and the advert named.
    /// </summary>
    /// <remarks>
    /// Built on the list rather than written again, because the interesting part of both is the
    /// same two joins — and a second copy of "a candidate since removed" is a second place for
    /// that sentence to drift.
    /// </remarks>
    public async Task<WaitingApplicationRow?> ApplicationAsync(
        Guid id, CancellationToken cancellationToken = default) =>
        (await ApplicationsAsync(cancellationToken: cancellationToken))
            .FirstOrDefault(one => one.Id == id);

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
    /// <summary>
    /// The exercises that have been set, the ones still waiting first.
    /// </summary>
    /// <remarks>
    /// Outstanding before settled, because this screen is worked rather than browsed: somebody
    /// opens it to see what is waiting on them, and a list ordered by date puts last month's
    /// marked exercises above this week's unmarked one.
    ///
    /// Capped, like every list in this codebase that grows without bound. A firm that has set
    /// two hundred exercises has a hiring history worth a report rather than a page.
    /// </remarks>
    public async Task<List<AssessmentRow>> AssessmentsAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await database.Assessments
            .AsNoTracking()
            .OrderBy(assessment => assessment.Status == AssessmentStatus.Marked
                || assessment.Status == AssessmentStatus.Cancelled)
            .ThenBy(assessment => assessment.DueBy)
            .Take(200)
            .Select(assessment => new
            {
                assessment.Id,
                assessment.ApplicationId,
                assessment.Kind,
                assessment.Title,
                assessment.Instructions,
                assessment.DueBy,
                assessment.Status,
                assessment.SubmissionUrl,
                assessment.Result,
                assessment.MarkerNotes,
                assessment.CancelledBecause,
                assessment.SubmittedAt,
            })
            .ToListAsync(cancellationToken);

        var applicationIds = rows.Select(row => row.ApplicationId).Distinct().ToList();

        /*
         * The candidate's name and the post, resolved in one second query rather than joined —
         * the pattern the rest of this file uses, and the reason is the same: two small reads
         * are plain to follow where the join costs a reader five minutes.
         */
        var about = await database.Applications
            .AsNoTracking()
            .Where(application => applicationIds.Contains(application.Id))
            .Select(application => new
            {
                application.Id,
                Candidate = database.Candidates
                    .Where(candidate => candidate.Id == application.CandidateId)
                    .Select(candidate => candidate.FullName)
                    .FirstOrDefault(),
                Posting = database.Postings
                    .Where(posting => posting.Id == application.PostingId)
                    .Select(posting => posting.Title)
                    .FirstOrDefault(),
            })
            .ToDictionaryAsync(row => row.Id, cancellationToken);

        return rows.Select(row => new AssessmentRow(
            row.Id,
            row.ApplicationId,
            about.TryGetValue(row.ApplicationId, out var who)
                ? who.Candidate ?? "Somebody since removed"
                : "Somebody since removed",
            about.TryGetValue(row.ApplicationId, out var post)
                ? post.Posting ?? "A post since removed"
                : "A post since removed",
            row.Kind,
            row.Title,
            row.Instructions,
            row.DueBy,
            row.Status,
            row.SubmissionUrl,
            row.Result,
            row.MarkerNotes,
            row.CancelledBecause,
            row.SubmittedAt)).ToList();
    }

}

/// <summary>One exercise, as the hiring screen shows it.</summary>
public sealed record AssessmentRow(
    Guid Id,
    Guid ApplicationId,
    string CandidateName,
    string PostingTitle,
    AssessmentKind Kind,
    string Title,
    string Instructions,
    DateOnly DueBy,
    AssessmentStatus Status,
    string? SubmissionUrl,
    Recommendation? Result,
    string? MarkerNotes,
    string? CancelledBecause,
    DateTimeOffset? SubmittedAt)
{
    /// <summary>Waiting on somebody — out with the candidate, or back and unmarked.</summary>
    public bool IsOutstanding =>
        Status is AssessmentStatus.Assigned or AssessmentStatus.Submitted;

    /// <summary>
    /// Whether the deadline has gone by with nothing handed in.
    /// </summary>
    /// <remarks>
    /// Takes the day rather than reading a clock, so the screen and any test agree about what
    /// "today" is. There is no stored Expired status for the same reason an invoice has no
    /// Overdue one: being late is what is true at the moment somebody looks.
    /// </remarks>
    public bool IsLateOn(DateOnly today) =>
        Status == AssessmentStatus.Assigned && DueBy < today;

    /// <summary>Whether it was handed in after the deadline, which the two dates show.</summary>
    public bool WasLate =>
        SubmittedAt is { } handed && DateOnly.FromDateTime(handed.UtcDateTime) > DueBy;
}

public sealed record OpeningRow(
    Guid Id,
    string Title,
    string Slug,
    string Summary,
    string Description,
    string? Location);

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

/// <summary>An application, with enough around it to act on without opening it.</summary>
public sealed record WaitingApplicationRow(
    Guid Id,

    /// <summary>
    /// The candidate, so the list can link to their profile.
    /// </summary>
    /// <remarks>
    /// The row carried a name and an email and no way to reach the record they came from,
    /// which made the candidate profile a page nothing could link to.
    /// </remarks>
    Guid CandidateId,
    string CandidateName,
    string CandidateEmail,
    string PostingTitle,
    Guid PostingId,
    ApplicationStatus Status,
    DateTimeOffset AppliedAt,
    bool HasCv);

public sealed record ApplicationRow(
    Guid Id,
    string CandidateName,
    string CandidateEmail,
    ApplicationStatus Status,
    DateTimeOffset AppliedAt);
