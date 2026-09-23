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

    public Task<Build?> BuildAsync(
        Guid repositoryId, string externalId, CancellationToken cancellationToken = default) =>
        context.Builds.FirstOrDefaultAsync(
            build => build.RepositoryId == repositoryId && build.ExternalId == externalId,
            cancellationToken);

    public Task<Deployment?> DeploymentAsync(
        Guid repositoryId, string externalId, CancellationToken cancellationToken = default) =>
        context.Deployments.FirstOrDefaultAsync(
            deployment => deployment.RepositoryId == repositoryId
                && deployment.ExternalId == externalId,
            cancellationToken);

    /// <remarks>
    /// Returns the link of whichever commit matches, and nothing if none does. A build can
    /// legitimately arrive for a commit this system never saw — the push delivery
    /// dead-lettered, or the repository was connected after the branch was pushed — and the
    /// build is still worth recording unattached.
    /// </remarks>
    public async Task<Guid?> WorkForCommitAsync(
        string sha, CancellationToken cancellationToken = default) =>
        await context.Commits
            .AsNoTracking()
            .Where(commit => commit.Sha == sha || commit.Sha == sha.ToUpperInvariant())
            .Select(commit => commit.WorkItemId)
            .FirstOrDefaultAsync(cancellationToken);

    public void Add(Build build) => context.Builds.Add(build);

    public void Add(Deployment deployment) => context.Deployments.Add(deployment);

    public Task<Contributor?> ContributorAsync(
        GitProvider provider, string handle, CancellationToken cancellationToken = default) =>
        context.Contributors.FirstOrDefaultAsync(
            one => one.Provider == provider && one.Handle.ToLower() == handle.ToLower(),
            cancellationToken);

    public Task<Contributor?> FindContributorAsync(
        Guid id, CancellationToken cancellationToken = default) =>
        context.Contributors.FirstOrDefaultAsync(one => one.Id == id, cancellationToken);

    public Task<List<Contributor>> ContributorsAsync(
        CancellationToken cancellationToken = default) =>
        context.Contributors.OrderBy(one => one.Handle).ToListAsync(cancellationToken);

    public async Task<List<UnclaimedHandle>> UnclaimedAsync(
        CancellationToken cancellationToken = default)
    {
        var claimed = await context.Contributors
            .Select(one => one.Handle)
            .ToListAsync(cancellationToken);

        /*
         * Grouped in the database. A firm with a long history has hundreds of
         * thousands of commits, and pulling the authors back to group them here would
         * read the column for every one of them to produce a list of a dozen names.
         */
        var seen = await context.Commits
            .Where(commit => !claimed.Contains(commit.Author))
            .GroupBy(commit => commit.Author)
            .Select(group => new { Handle = group.Key, Commits = group.Count() })
            .OrderByDescending(group => group.Commits)
            .Take(50)
            .ToListAsync(cancellationToken);

        /*
         * Reported as GitHub's, because a commit does not record which host it came
         * from — only the repository does, and the same login can appear on two. The
         * screen says so, and claiming the wrong one is corrected by claiming again.
         */
        return [.. seen.Select(one => new UnclaimedHandle(
            GitProvider.GitHub, one.Handle, one.Commits))];
    }

    public void Add(Contributor contributor) => context.Contributors.Add(contributor);

    public void Remove(Contributor contributor) => context.Contributors.Remove(contributor);

    public void Add(Repository repository) => context.Repositories.Add(repository);

    public void Add(WebhookDelivery delivery) => context.Deliveries.Add(delivery);

    public void Add(PullRequest pullRequest) => context.PullRequests.Add(pullRequest);

    public void Add(Commit commit) => context.Commits.Add(commit);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        context.SaveChangesAsync(cancellationToken);
}
