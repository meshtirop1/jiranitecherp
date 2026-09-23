using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Engineering;

/// <summary>The reads the repository screens do, kept out of the screens.</summary>
public sealed class EngineeringQueries(AppDbContext database)
{
    /// <summary>
    /// Every repository, with enough beside it to tell whether it is working.
    /// </summary>
    /// <remarks>
    /// The counts and the last delivery are the whole value of this screen. A
    /// list of repository names tells nobody anything they could not get from
    /// GitHub; "connected three weeks ago, nothing received since" is the thing
    /// worth having, and it is the thing a naive list would hide.
    /// </remarks>
    public async Task<List<RepositoryRow>> RepositoriesAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await database.Repositories
            .AsNoTracking()
            .OrderBy(repository => repository.DisconnectedAt == null ? 0 : 1)
            .ThenBy(repository => repository.Owner)
            .ThenBy(repository => repository.Name)
            .Select(repository => new
            {
                repository.Id,
                repository.Provider,
                repository.Owner,
                repository.Name,
                repository.ProjectId,
                repository.SecretHash,
                repository.ConnectedAt,
                repository.LastDeliveryAt,
                repository.DisconnectedAt,
                Project = database.Projects
                    .Where(project => project.Id == repository.ProjectId)
                    .Select(project => project.Name)
                    .FirstOrDefault(),
                Commits = database.Commits.Count(
                    commit => commit.RepositoryId == repository.Id),
                Open = database.PullRequests.Count(
                    pullRequest => pullRequest.RepositoryId == repository.Id
                        && pullRequest.State == PullRequestState.Open),
                Failing = database.Deliveries.Count(
                    delivery => delivery.RepositoryId == repository.Id
                        && delivery.Status == DeliveryStatus.DeadLettered),
            })
            .ToListAsync(cancellationToken);

        return [.. rows.Select(row => new RepositoryRow(
            row.Id,
            row.Provider,
            $"{row.Owner}/{row.Name}",
            row.Project,
            row.SecretHash,
            row.ConnectedAt,
            row.LastDeliveryAt,
            row.DisconnectedAt is null,
            row.Commits,
            row.Open,
            row.Failing))];
    }

    /// <summary>The delivery log, newest first.</summary>
    public async Task<List<DeliveryRow>> DeliveriesAsync(
        DeliveryStatus? status = null,
        Guid? repositoryId = null,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        var query = database.Deliveries.AsNoTracking();

        if (status is { } wanted)
        {
            query = query.Where(delivery => delivery.Status == wanted);
        }

        if (repositoryId is { } repository)
        {
            query = query.Where(delivery => delivery.RepositoryId == repository);
        }

        var rows = await query
            .OrderByDescending(delivery => delivery.ReceivedAt)
            .Take(take)
            .Select(delivery => new
            {
                delivery.Id,
                delivery.Provider,
                delivery.Event,
                delivery.Status,
                delivery.Attempts,
                delivery.ReceivedAt,
                delivery.Error,
                Repository = database.Repositories
                    .Where(repository => repository.Id == delivery.RepositoryId)
                    .Select(repository => repository.Owner + "/" + repository.Name)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        return [.. rows.Select(row => new DeliveryRow(
            row.Id,
            row.Provider,
            row.Event,
            row.Repository,
            row.Status,
            row.Attempts,
            row.ReceivedAt,
            row.Error))];
    }

    /// <summary>How many deliveries have given up, for the badge on the menu.</summary>
    public Task<int> DeadLetteredAsync(CancellationToken cancellationToken = default) =>
        database.Deliveries.CountAsync(
            delivery => delivery.Status == DeliveryStatus.DeadLettered, cancellationToken);

    /// <summary>One delivery, with the body, for whoever has to work out why.</summary>
    public async Task<DeliveryDetail?> DeliveryAsync(
        Guid id, CancellationToken cancellationToken = default)
    {
        var delivery = await database.Deliveries
            .AsNoTracking()
            .FirstOrDefaultAsync(one => one.Id == id, cancellationToken);

        if (delivery is null)
        {
            return null;
        }

        var repository = delivery.RepositoryId is { } repositoryId
            ? await database.Repositories
                .AsNoTracking()
                .Where(one => one.Id == repositoryId)
                .Select(one => one.Owner + "/" + one.Name)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        return new DeliveryDetail(
            delivery.Id,
            delivery.Provider,
            delivery.Event,
            delivery.ExternalId,
            repository,
            delivery.Status,
            delivery.Attempts,
            delivery.ReceivedAt,
            delivery.HandledAt,
            delivery.Error,
            delivery.Payload);
    }

    /// <summary>
    /// What was done against one piece of work.
    /// </summary>
    /// <remarks>
    /// The payoff for the whole integration, and the reason the work item page
    /// is where this belongs. Somebody looking at a task can see the branch, the
    /// commits and the pull request without anybody having written a status
    /// update — which is the promise the brief opens with.
    /// </remarks>
    public async Task<WorkEvidence> EvidenceForAsync(
        Guid workItemId, CancellationToken cancellationToken = default)
    {
        var commits = await database.Commits
            .AsNoTracking()
            .Where(commit => commit.WorkItemId == workItemId)
            .OrderByDescending(commit => commit.At)
            .Take(50)
            .Select(commit => new CommitRow(
                commit.Sha,
                commit.Message,
                commit.Author,
                commit.Branch,
                commit.At))
            .ToListAsync(cancellationToken);

        var pullRequests = await database.PullRequests
            .AsNoTracking()
            .Where(pullRequest => pullRequest.WorkItemId == workItemId)
            .OrderByDescending(pullRequest => pullRequest.OpenedAt)
            .Select(pullRequest => new
            {
                pullRequest.Number,
                pullRequest.Title,
                pullRequest.Author,
                pullRequest.Branch,
                pullRequest.State,
                pullRequest.OpenedAt,
                Repository = database.Repositories
                    .Where(repository => repository.Id == pullRequest.RepositoryId)
                    .Select(repository => repository.Owner + "/" + repository.Name)
                    .FirstOrDefault(),
                Reviews = pullRequest.Reviews.Count,
                Approved = pullRequest.Reviews
                    .Any(review => review.Verdict == ReviewVerdict.Approved),
                Blocked = pullRequest.Reviews
                    .Any(review => review.Verdict == ReviewVerdict.ChangesRequested),
            })
            .ToListAsync(cancellationToken);

        return new WorkEvidence(
            commits,
            [.. pullRequests.Select(row => new PullRequestRow(
                row.Number,
                row.Title,
                row.Author,
                row.Branch,
                row.Repository,
                row.State,
                row.OpenedAt,
                row.Reviews,
                row.Approved && !row.Blocked))]);
    }
}

public sealed record RepositoryRow(
    Guid Id,
    GitProvider Provider,
    string FullName,
    string? Project,
    string SecretHash,
    DateTimeOffset ConnectedAt,
    DateTimeOffset? LastDeliveryAt,
    bool IsWatched,
    int Commits,
    int OpenPullRequests,
    int Failing);

public sealed record DeliveryRow(
    Guid Id,
    GitProvider Provider,
    string Event,
    string? Repository,
    DeliveryStatus Status,
    int Attempts,
    DateTimeOffset ReceivedAt,
    string? Error);

public sealed record DeliveryDetail(
    Guid Id,
    GitProvider Provider,
    string Event,
    string ExternalId,
    string? Repository,
    DeliveryStatus Status,
    int Attempts,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? HandledAt,
    string? Error,
    string Payload);

public sealed record CommitRow(
    string Sha,
    string Message,
    string Author,
    string Branch,
    DateTimeOffset At)
{
    public string Short => Sha.Length > 7 ? Sha[..7] : Sha;
}

public sealed record PullRequestRow(
    int Number,
    string Title,
    string Author,
    string Branch,
    string? Repository,
    PullRequestState State,
    DateTimeOffset OpenedAt,
    int Reviews,
    bool IsApproved);

/// <summary>What the repositories say was done against a piece of work.</summary>
public sealed record WorkEvidence(
    IReadOnlyList<CommitRow> Commits,
    IReadOnlyList<PullRequestRow> PullRequests)
{
    public bool IsEmpty => Commits.Count == 0 && PullRequests.Count == 0;
}
