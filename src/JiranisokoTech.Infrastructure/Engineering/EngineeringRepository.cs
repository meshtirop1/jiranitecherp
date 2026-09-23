using JiranisokoTech.Application.Engineering;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Repository = JiranisokoTech.Domain.Engineering.Repository;

namespace JiranisokoTech.Infrastructure.Engineering;

public sealed class EngineeringRepository(AppDbContext context) : IEngineeringRepository
{
    public Task<Repository?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        context.Repositories.FirstOrDefaultAsync(
            repository => repository.Id == id, cancellationToken);

    /// <summary>
    /// The repository a delivery named.
    /// </summary>
    /// <remarks>
    /// Split into owner and name and compared case-insensitively, rather than
    /// matching a computed FullName — which is not a column and cannot be
    /// queried. The comparison is deliberate: GitHub treats these names without
    /// regard to case and will happily send a payload spelling a repository
    /// differently from however it was typed here, and a miss means the
    /// delivery is filed as belonging to nothing.
    /// </remarks>
    public Task<Repository?> ByFullNameAsync(
        GitProvider provider, string fullName, CancellationToken cancellationToken = default)
    {
        var split = fullName.Split('/', 2);

        if (split.Length != 2)
        {
            return Task.FromResult<Repository?>(null);
        }

        var owner = split[0];
        var name = split[1];

        return context.Repositories.FirstOrDefaultAsync(
            repository => repository.Provider == provider
                && repository.Owner.ToLower() == owner.ToLower()
                && repository.Name.ToLower() == name.ToLower(),
            cancellationToken);
    }

    public Task<List<Repository>> WatchedAsync(CancellationToken cancellationToken = default) =>
        context.Repositories
            .Where(repository => repository.DisconnectedAt == null)
            .OrderBy(repository => repository.Owner)
            .ThenBy(repository => repository.Name)
            .ToListAsync(cancellationToken);

    public Task<List<Repository>> AllAsync(CancellationToken cancellationToken = default) =>
        context.Repositories
            .OrderBy(repository => repository.DisconnectedAt == null ? 0 : 1)
            .ThenBy(repository => repository.Owner)
            .ThenBy(repository => repository.Name)
            .ToListAsync(cancellationToken);

    public Task<bool> DeliveryKnownAsync(
        GitProvider provider, string externalId, CancellationToken cancellationToken = default) =>
        context.Deliveries.AnyAsync(
            delivery => delivery.Provider == provider && delivery.ExternalId == externalId,
            cancellationToken);

    public Task<WebhookDelivery?> FindDeliveryAsync(
        Guid id, CancellationToken cancellationToken = default) =>
        context.Deliveries.FirstOrDefaultAsync(
            delivery => delivery.Id == id, cancellationToken);

    public Task<List<WebhookDelivery>> WaitingDeliveriesAsync(
        int batchSize, CancellationToken cancellationToken = default) =>
        context.Deliveries
            .Where(delivery => delivery.Status == DeliveryStatus.Received
                || delivery.Status == DeliveryStatus.Failed)
            .OrderBy(delivery => delivery.ReceivedAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// A pull request with its reviews loaded.
    /// </summary>
    /// <remarks>
    /// Reviews are an owned collection, so EF loads them with the pull request
    /// and no Include is needed. Named here because the alternative — a pull
    /// request whose Reviews came back empty — would make an approved pull
    /// request look unapproved, and a release gate would refuse it.
    /// </remarks>
    public Task<PullRequest?> PullRequestAsync(
        Guid repositoryId, int number, CancellationToken cancellationToken = default) =>
        context.PullRequests.FirstOrDefaultAsync(
            pullRequest => pullRequest.RepositoryId == repositoryId
                && pullRequest.Number == number,
            cancellationToken);

    public Task<bool> CommitKnownAsync(
        string sha, CancellationToken cancellationToken = default) =>
        context.Commits.AnyAsync(commit => commit.Sha == sha, cancellationToken);

    public void Add(Repository repository) => context.Repositories.Add(repository);

    public void Add(WebhookDelivery delivery) => context.Deliveries.Add(delivery);

    public void Add(PullRequest pullRequest) => context.PullRequests.Add(pullRequest);

    public void Add(Commit commit) => context.Commits.Add(commit);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        context.SaveChangesAsync(cancellationToken);
}
