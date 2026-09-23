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
    /// What the repositories say about the last few weeks.
    /// </summary>
    /// <remarks>
    /// Section 38 asked for engineering metrics, and the choice of which four is the
    /// substance of this method.
    ///
    /// What is deliberately absent: commits per person, lines changed, and anything
    /// else that ranks individuals. Those numbers are easy to produce here and they
    /// measure the wrong thing — a developer who spends a week deleting code looks
    /// idle, and one who reformats a file looks heroic. Published on a report they
    /// change behaviour within a fortnight, and the behaviour they produce is more
    /// commits rather than more delivered work.
    ///
    /// What is here instead describes the flow of work rather than the people in it:
    /// how much is moving, how long it waits for review, and how much is sitting open.
    /// Those are questions a head of department can act on without them becoming a
    /// league table.
    /// </remarks>
    public async Task<EngineeringState> StateAsync(
        DateOnly today, int overDays = 28, CancellationToken cancellationToken = default)
    {
        var since = new DateTimeOffset(
            today.AddDays(-overDays).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var commits = await database.Commits
            .AsNoTracking()
            .CountAsync(commit => commit.At >= since, cancellationToken);

        var attached = await database.Commits
            .AsNoTracking()
            .CountAsync(commit => commit.At >= since && commit.WorkItemId != null,
                cancellationToken);

        var opened = await database.PullRequests
            .AsNoTracking()
            .CountAsync(pullRequest => pullRequest.OpenedAt >= since, cancellationToken);

        var merged = await database.PullRequests
            .AsNoTracking()
            .Where(pullRequest => pullRequest.State == PullRequestState.Merged
                && pullRequest.ClosedAt >= since)
            .Select(pullRequest => new { pullRequest.OpenedAt, pullRequest.ClosedAt })
            .ToListAsync(cancellationToken);

        var open = await database.PullRequests
            .AsNoTracking()
            .Where(pullRequest => pullRequest.State == PullRequestState.Open)
            .Select(pullRequest => pullRequest.OpenedAt)
            .ToListAsync(cancellationToken);

        /*
         * The median rather than the mean, and it is not a detail. One pull request
         * left open over Christmas drags a mean into uselessness, and the number
         * people want is "how long does this usually take" — which is the median and
         * has never been the mean.
         */
        var toMerge = merged
            .Where(one => one.ClosedAt is not null)
            .Select(one => (one.ClosedAt!.Value - one.OpenedAt).TotalHours)
            .OrderBy(hours => hours)
            .ToList();

        var now = new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        return new EngineeringState(
            overDays,
            commits,
            attached,
            opened,
            merged.Count,
            open.Count,
            Median(toMerge),
            // The oldest thing waiting for a review, which is the one number on this
            // list that names something somebody should do today.
            open.Count == 0 ? null : (int)(now - open.Min()).TotalDays);
    }

    /// <summary>The middle value, or nothing when there is nothing to take one of.</summary>
    private static double? Median(IReadOnlyList<double> sorted) => sorted.Count switch
    {
        0 => null,
        var count when count % 2 == 1 => sorted[count / 2],
        var count => (sorted[(count / 2) - 1] + sorted[count / 2]) / 2,
    };

    /// <summary>
    /// What the repositories say one person did on one day.
    /// </summary>
    /// <remarks>
    /// Section 21 asked for time to be detected automatically from Git rather than
    /// typed, and this is as far as that can honestly go. What it does <em>not</em>
    /// do is turn commits into hours.
    ///
    /// The temptation is obvious: first commit at 09:14, last at 17:32, therefore
    /// eight hours and eighteen minutes. It is also wrong in a way that matters more
    /// here than in most systems, because these hours are approved and then billed to
    /// a client. A commit is a moment, not a duration. The gap between two of them
    /// contains lunch, a meeting, an afternoon on somebody else's problem, and the
    /// hour spent thinking before the first one is not in the record at all. A number
    /// derived from those timestamps would be a guess wearing the clothes of a
    /// measurement, and it would end up on an invoice.
    ///
    /// So this returns evidence and the person writes the number. It removes the part
    /// that is genuinely hard to remember — which projects, and roughly when — and
    /// leaves the part only they can answer.
    /// </remarks>
    public async Task<DayOfWork> DayOfWorkAsync(
        Guid employeeId, DateOnly on, CancellationToken cancellationToken = default)
    {
        /*
         * Matched through the claimed handles rather than by name. A provider login
         * that resembles somebody's name is not evidence that it is them, and
         * guessing wrong would put another person's work on this timesheet.
         */
        var handles = await database.Contributors
            .AsNoTracking()
            .Where(contributor => contributor.EmployeeId == employeeId)
            .Select(contributor => contributor.Handle)
            .ToListAsync(cancellationToken);

        if (handles.Count == 0)
        {
            return new DayOfWork([], [], null, null, false);
        }

        var from = new DateTimeOffset(on.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var until = from.AddDays(1);

        var commits = await database.Commits
            .AsNoTracking()
            .Where(commit => handles.Contains(commit.Author)
                && commit.At >= from && commit.At < until)
            .OrderBy(commit => commit.At)
            .Select(commit => new
            {
                commit.Sha,
                commit.Message,
                commit.Branch,
                commit.At,
                commit.WorkItemId,
                ProjectId = database.Repositories
                    .Where(repository => repository.Id == commit.RepositoryId)
                    .Select(repository => repository.ProjectId)
                    .FirstOrDefault(),
                Repository = database.Repositories
                    .Where(repository => repository.Id == commit.RepositoryId)
                    .Select(repository => repository.Owner + "/" + repository.Name)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var merged = await database.PullRequests
            .AsNoTracking()
            .Where(pullRequest => handles.Contains(pullRequest.Author)
                && pullRequest.ClosedAt >= from && pullRequest.ClosedAt < until
                && pullRequest.State == PullRequestState.Merged)
            .Select(pullRequest => pullRequest.Title)
            .ToListAsync(cancellationToken);

        /*
         * A work item's project is preferred over the repository's. A shared library
         * has no project of its own, and a commit on it that names a work item was
         * still done for whatever that work belongs to.
         */
        var workItems = commits
            .Where(commit => commit.WorkItemId is not null)
            .Select(commit => commit.WorkItemId!.Value)
            .Distinct()
            .ToList();

        var throughWork = await database.WorkItems
            .AsNoTracking()
            .Where(item => workItems.Contains(item.Id))
            .Select(item => new { item.Id, item.ProjectId })
            .ToDictionaryAsync(item => item.Id, item => item.ProjectId, cancellationToken);

        var projects = await database.Projects
            .AsNoTracking()
            .ToDictionaryAsync(project => project.Id, project => project.Name, cancellationToken);

        var touched = commits
            .Select(commit => commit.WorkItemId is { } work
                    && throughWork.TryGetValue(work, out var through) && through is not null
                ? through
                : commit.ProjectId)
            .Where(project => project is not null)
            .Select(project => project!.Value)
            .Distinct()
            .Select(project => new TouchedProject(
                project, projects.GetValueOrDefault(project) ?? "(unknown)"))
            .ToList();

        return new DayOfWork(
            [.. commits.Select(commit => new CommitRow(
                commit.Sha, commit.Message, "you", commit.Branch, commit.At))],
            touched,
            commits.Count > 0 ? commits[0].At : null,
            commits.Count > 0 ? commits[^1].At : null,
            true) with { Merged = merged };
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

/// <summary>
/// What the repositories say one person did on one day.
/// </summary>
/// <remarks>
/// Evidence for somebody filling in a timesheet, and deliberately not a number of
/// hours. See DayOfWorkAsync for why deriving one from commit timestamps would be a
/// guess wearing the clothes of a measurement — on a figure that ends up invoiced.
/// </remarks>
public sealed record DayOfWork(
    IReadOnlyList<CommitRow> Commits,
    IReadOnlyList<TouchedProject> Projects,
    DateTimeOffset? First,
    DateTimeOffset? Last,
    bool HasClaimedHandle)
{
    public IReadOnlyList<string> Merged { get; init; } = [];

    public bool IsEmpty => Commits.Count == 0 && Merged.Count == 0;

    /// <summary>
    /// The span between the first and last commit, said as a range and never as a
    /// total.
    /// </summary>
    /// <remarks>
    /// Phrased this way on the screen too. "09:14 to 17:32" is a fact; "8h 18m" is an
    /// assertion about how somebody spent their day that nothing here is entitled to
    /// make.
    /// </remarks>
    public string? Span => First is { } first && Last is { } last && first != last
        ? $"{first.ToLocalTime():HH:mm} to {last.ToLocalTime():HH:mm}"
        : First?.ToLocalTime().ToString("HH:mm");
}

public sealed record TouchedProject(Guid Id, string Name);

/// <summary>What the repositories say was done against a piece of work.</summary>
public sealed record WorkEvidence(
    IReadOnlyList<CommitRow> Commits,
    IReadOnlyList<PullRequestRow> PullRequests)
{
    public bool IsEmpty => Commits.Count == 0 && PullRequests.Count == 0;
}

/// <summary>
/// The flow of work through the repositories, over a window.
/// </summary>
/// <remarks>
/// Nothing here is per person, deliberately. See StateAsync.
/// </remarks>
public sealed record EngineeringState(
    int OverDays,
    int Commits,
    int CommitsAttachedToWork,
    int PullRequestsOpened,
    int PullRequestsMerged,
    int PullRequestsOpen,
    double? MedianHoursToMerge,
    int? OldestOpenDays)
{
    public bool NothingRecorded => Commits == 0 && PullRequestsOpened == 0;

    /// <summary>
    /// How much of the work in the repositories is tied back to a task.
    /// </summary>
    /// <remarks>
    /// The health figure for the integration itself rather than for the engineering.
    /// A low share means people are not naming their branches after the work, so the
    /// board is telling a less complete story than it appears to — and that is worth
    /// seeing before somebody makes a decision on it.
    /// </remarks>
    public int AttachedShare => Commits == 0
        ? 0
        : (int)Math.Round(CommitsAttachedToWork * 100.0 / Commits);

    public string? TypicalMerge => MedianHoursToMerge switch
    {
        null => null,
        < 24 => $"{MedianHoursToMerge.Value:0.#} hours",
        _ => $"{MedianHoursToMerge.Value / 24:0.#} days",
    };
}
