using JiranisokoTech.Domain.Recruitment;

namespace JiranisokoTech.Application.Recruitment;

public interface IRecruitmentRepository
{
    Task<JobRequisition?> FindRequisitionAsync(Guid id, CancellationToken cancellationToken = default);

    Task<JobPosting?> FindPostingAsync(Guid id, CancellationToken cancellationToken = default);

    Task<JobApplication?> FindApplicationAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Candidate?> FindCandidateAsync(
        Guid id, CancellationToken cancellationToken = default);

    Task<Candidate?> FindCandidateByEmailAsync(
        string email, CancellationToken cancellationToken = default);

    Task<bool> PostingSlugTakenAsync(string slug, CancellationToken cancellationToken = default);

    /// <summary>Adverts still up against this requisition.</summary>
    Task<List<JobPosting>> OpenPostingsForAsync(
        Guid requisitionId, CancellationToken cancellationToken = default);

    Task<TechnicalAssessment?> FindAssessmentAsync(
        Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// An exercise is still out on this application, or is back and unmarked.
    /// </summary>
    /// <remarks>
    /// Asked before every move to an offer, whether or not an exercise was ever set, so it has
    /// to be cheap — it is an AnyAsync over the (ApplicationId, Status) index and nothing more.
    /// An offer sent while the work is still with the candidate is the waste this whole step
    /// exists to prevent.
    /// </remarks>
    Task<bool> AssessmentOutstandingAsync(
        Guid applicationId, CancellationToken cancellationToken = default);

    void Add(TechnicalAssessment assessment);

    void Add(JobRequisition requisition);

    void Add(JobPosting posting);

    void Add(Candidate candidate);

    void Add(JobApplication application);

    Task SaveAsync(CancellationToken cancellationToken = default);
}
