using JiranisokoTech.Domain.Engineering;

namespace JiranisokoTech.Application.Engineering;

public interface IEngineeringRepository
{
    Task<Repository?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The repository a delivery named, as owner/name.</summary>
    /// <remarks>
    /// Matched case-insensitively. Git hosts treat repository names as
    /// case-insensitive and people type them as they remember them, so a
    /// repository connected as Jiranisoko/ERP that stopped receiving anything
    /// the day a payload said jiranisoko/erp would be a very quiet bug.
    /// </remarks>
    Task<Repository?> ByFullNameAsync(
        GitProvider provider, string fullName, CancellationToken cancellationToken = default);

    Task<List<Repository>> WatchedAsync(CancellationToken cancellationToken = default);

    Task<List<Repository>> AllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Has this exact delivery already been recorded?
    /// </summary>
    /// <remarks>
    /// The cheap half of the idempotency guarantee. The real guarantee is the
    /// unique index — two deliveries arriving at once would both pass this check
    /// — but the check means the ordinary retry is answered without provoking a
    /// constraint violation and a rolled-back transaction.
    /// </remarks>
    Task<bool> DeliveryKnownAsync(
        GitProvider provider, string externalId, CancellationToken cancellationToken = default);

    Task<WebhookDelivery?> FindDeliveryAsync(
        Guid id, CancellationToken cancellationToken = default);

    /// <summary>Deliveries waiting to be handled, oldest first.</summary>
    /// <remarks>
    /// Oldest first because a push and the pull request that follows it arrive
    /// in that order, and handling them backwards would attach commits to a
    /// branch whose pull request already knows better.
    /// </remarks>
    Task<List<WebhookDelivery>> WaitingDeliveriesAsync(
        int batchSize, CancellationToken cancellationToken = default);

    Task<PullRequest?> PullRequestAsync(
        Guid repositoryId, int number, CancellationToken cancellationToken = default);

    /// <summary>Is this commit already recorded?</summary>
    /// <remarks>
    /// A push carries every commit on the branch that is new to the remote, and
    /// the same commit arrives again when the branch is merged, force-pushed or
    /// pushed to a second branch. Without this check a repository's history
    /// accumulates duplicates at exactly the moments people are busiest.
    /// </remarks>
    Task<bool> CommitKnownAsync(
        string sha, CancellationToken cancellationToken = default);

    Task<Contributor?> ContributorAsync(
        GitProvider provider, string handle, CancellationToken cancellationToken = default);

    Task<Contributor?> FindContributorAsync(
        Guid id, CancellationToken cancellationToken = default);

    Task<List<Contributor>> ContributorsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Logins that have committed here and belong to nobody yet.
    /// </summary>
    /// <remarks>
    /// The list that makes claiming a handle a five-second job rather than a matter of
    /// remembering what somebody's GitHub name is. Anything on it is work being
    /// recorded against nobody.
    /// </remarks>
    Task<List<UnclaimedHandle>> UnclaimedAsync(CancellationToken cancellationToken = default);

    void Add(Contributor contributor);

    void Remove(Contributor contributor);

    void Add(Repository repository);

    void Add(WebhookDelivery delivery);

    void Add(PullRequest pullRequest);

    void Add(Commit commit);

    Task SaveAsync(CancellationToken cancellationToken = default);
}

/// <summary>A login seen in the repositories that belongs to nobody yet.</summary>
public sealed record UnclaimedHandle(GitProvider Provider, string Handle, int Commits);
