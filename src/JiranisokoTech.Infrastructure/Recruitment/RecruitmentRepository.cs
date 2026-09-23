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

    public void Add(JobApplication application) => database.Applications.Add(application);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        database.SaveChangesAsync(cancellationToken);
}
