using JiranisokoTech.Application.Approvals;
using JiranisokoTech.Domain.Approvals;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Approvals;

public sealed class ApprovalRepository(AppDbContext database) : IApprovalRepository
{
    /// <summary>
    /// Loaded with its steps, always.
    /// </summary>
    /// <remarks>
    /// Every rule on the chain is about the steps as a set — whose turn it is,
    /// whether this is the last one waiting — so a request without them cannot
    /// answer anything. Owned collections are loaded with their owner by
    /// default; the include is written out so that a later change to that
    /// default does not silently produce a chain that thinks it has no steps.
    /// </remarks>
    public Task<ApprovalRequest?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.Approvals
            .Include(request => request.Steps)
            .FirstOrDefaultAsync(request => request.Id == id, cancellationToken);

    public Task<List<ApprovalRequest>> ForSubjectAsync(
        string subjectType, Guid subjectId, CancellationToken cancellationToken = default) =>
        database.Approvals
            .Include(request => request.Steps)
            .Where(request => request.SubjectType == subjectType && request.SubjectId == subjectId)
            .OrderByDescending(request => request.RequestedAt)
            .ToListAsync(cancellationToken);

    public Task<bool> IsPendingAsync(
        string subjectType,
        Guid subjectId,
        string action,
        CancellationToken cancellationToken = default) =>
        database.Approvals.AnyAsync(
            request => request.SubjectType == subjectType
                && request.SubjectId == subjectId
                && request.Action == action
                && request.Status == ApprovalStatus.Pending,
            cancellationToken);

    public void Add(ApprovalRequest request) => database.Approvals.Add(request);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        database.SaveChangesAsync(cancellationToken);
}
