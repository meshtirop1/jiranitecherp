using JiranisokoTech.Domain.Approvals;

namespace JiranisokoTech.Application.Approvals;

public interface IApprovalRepository
{
    Task<ApprovalRequest?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Chains over one thing, newest first.</summary>
    Task<List<ApprovalRequest>> ForSubjectAsync(
        string subjectType, Guid subjectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Is anything still pending over this subject for this action?
    /// </summary>
    /// <remarks>
    /// Asked before opening a new one. Two live chains over the same decision
    /// means two different answers can come back, and whichever arrives second
    /// silently overwrites the first.
    /// </remarks>
    Task<bool> IsPendingAsync(
        string subjectType,
        Guid subjectId,
        string action,
        CancellationToken cancellationToken = default);

    void Add(ApprovalRequest request);

    Task SaveAsync(CancellationToken cancellationToken = default);
}
