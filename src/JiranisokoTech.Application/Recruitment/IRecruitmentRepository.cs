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

    void Add(JobRequisition requisition);

    void Add(JobPosting posting);

    void Add(Candidate candidate);

    void Add(JobApplication application);

    Task SaveAsync(CancellationToken cancellationToken = default);
}
