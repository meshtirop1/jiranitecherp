using JiranisokoTech.Application.Recruitment;
using JiranisokoTech.Domain.Recruitment;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Recruitment;

public sealed class RecruitmentRepository(AppDbContext database) : IRecruitmentRepository
{
    public Task<JobRequisition?> FindRequisitionAsync(
        Guid id, CancellationToken cancellationToken = default) =>
        database.Requisitions.FirstOrDefaultAsync(
            requisition => requisition.Id == id, cancellationToken);

    public Task<JobPosting?> FindPostingAsync(
        Guid id, CancellationToken cancellationToken = default) =>
        database.Postings.FirstOrDefaultAsync(posting => posting.Id == id, cancellationToken);

    public Task<JobApplication?> FindApplicationAsync(
        Guid id, CancellationToken cancellationToken = default) =>
        database.Applications.FirstOrDefaultAsync(
            application => application.Id == id, cancellationToken);

    public Task<Candidate?> FindCandidateAsync(
        Guid id, CancellationToken cancellationToken = default) =>
        database.Candidates.FirstOrDefaultAsync(
            candidate => candidate.Id == id, cancellationToken);

    public Task<Candidate?> FindCandidateByEmailAsync(
        string email, CancellationToken cancellationToken = default) =>
        database.Candidates.FirstOrDefaultAsync(
            candidate => candidate.Email == email, cancellationToken);

    public Task<bool> PostingSlugTakenAsync(
        string slug, CancellationToken cancellationToken = default) =>
        database.Postings.AnyAsync(posting => posting.Slug == slug, cancellationToken);

    public Task<List<JobPosting>> OpenPostingsForAsync(
        Guid requisitionId, CancellationToken cancellationToken = default) =>
        database.Postings
            .Where(posting => posting.RequisitionId == requisitionId
                && posting.Status == PostingStatus.Published)
            .ToListAsync(cancellationToken);

    public void Add(JobRequisition requisition) => database.Requisitions.Add(requisition);

    public void Add(JobPosting posting) => database.Postings.Add(posting);

    public void Add(Candidate candidate) => database.Candidates.Add(candidate);

    public Task<Offer?> FindOfferAsync(
        Guid id, CancellationToken cancellationToken = default) =>
        database.Offers.FirstOrDefaultAsync(one => one.Id == id, cancellationToken);

    /// <remarks>
    /// Tracked rather than read-only, because the only reason to look an offer up by its link is
    /// to answer it — and a candidate pressing accept on a page that loaded a detached copy is a
    /// candidate whose answer goes nowhere.
    /// </remarks>
    public Task<Offer?> OfferByHashAsync(
        string tokenHash, CancellationToken cancellationToken = default) =>
        database.Offers.FirstOrDefaultAsync(
            one => one.TokenHash == tokenHash, cancellationToken);

    public Task<Offer?> LiveOfferForAsync(
        Guid applicationId, CancellationToken cancellationToken = default) =>
        database.Offers.FirstOrDefaultAsync(
            one => one.ApplicationId == applicationId
                && (one.Status == OfferStatus.Drafted || one.Status == OfferStatus.Sent),
            cancellationToken);

    public Task<List<Offer>> OffersForAsync(
        Guid applicationId, CancellationToken cancellationToken = default) =>
        database.Offers
            .AsNoTracking()
            .Where(one => one.ApplicationId == applicationId)
            .OrderByDescending(one => one.MadeAt)
            .ToListAsync(cancellationToken);

    public void Add(Offer offer) => database.Offers.Add(offer);

    public void Add(JobApplication application) => database.Applications.Add(application);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        database.SaveChangesAsync(cancellationToken);

    public Task<TechnicalAssessment?> FindAssessmentAsync(
        Guid id, CancellationToken cancellationToken = default) =>
        database.Assessments.FirstOrDefaultAsync(
            assessment => assessment.Id == id, cancellationToken);

    public Task<bool> AssessmentOutstandingAsync(
        Guid applicationId, CancellationToken cancellationToken = default) =>
        database.Assessments.AnyAsync(
            assessment => assessment.ApplicationId == applicationId
                && (assessment.Status == AssessmentStatus.Assigned
                    || assessment.Status == AssessmentStatus.Submitted),
            cancellationToken);

    public void Add(TechnicalAssessment assessment) => database.Assessments.Add(assessment);
}
