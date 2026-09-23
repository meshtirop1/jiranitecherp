using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Engineering;

/// <summary>The reads the repository screens do, kept out of the screens.</summary>
public sealed class EngineeringQueries(AppDbContext database)
{
    /// <summary>
    /// How many releases to each environment the environments page shows.
    /// </summary>
    /// <remarks>
    /// Eight, which is enough to see a pattern — three failed attempts on Tuesday and a
    /// success on Wednesday reads as a bad afternoon rather than as one deployment — and
    /// short enough that four of these boxes fit on a screen without scrolling.
    /// </remarks>
    private const int PerEnvironment = 8;

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

        /*
         * The day's deployments, found two ways and unioned.
         *
         * By the day's commit shas first, and that is the one that actually works. The
         * obvious approach — filter deployments by DeployedBy against the person's claimed
         * handles — looks right and would show an empty panel for most real deployments:
         * DeployedBy is whatever name the host put on whoever or whatever triggered the
         * release, and on a repository that deploys from a pipeline it is the name of a bot,
         * a token, or nothing at all.
         *
         * By handle as well, because when the host does name a person it is worth having:
         * somebody who spent the afternoon pressing the deploy button on work they committed
         * last week has nothing in the first set and everything in the second.
         *
         * The union is what the day was actually spent on, and neither half alone is.
         */
        var shas = commits.Select(commit => commit.Sha).ToList();

        var deployments = await database.Deployments
            .AsNoTracking()
            .Where(one => one.At >= from && one.At < until
                && (shas.Contains(one.Sha)
                    || (one.DeployedBy != null && handles.Contains(one.DeployedBy))))
            .OrderBy(one => one.At)
            .Select(one => new DeploymentRow(
                one.Environment,
                one.EnvironmentName,
                one.Sha,
                one.Branch,
                one.DeployedBy,
                one.State,
                one.At,
                one.Url))
            .ToListAsync(cancellationToken);

        return new DayOfWork(
            [.. commits.Select(commit => new CommitRow(
                commit.Sha, commit.Message, "you", commit.Branch, commit.At))],
            touched,
            commits.Count > 0 ? commits[0].At : null,
            commits.Count > 0 ? commits[^1].At : null,
            true) with { Merged = merged, Deployments = deployments };
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

        var builds = await database.Builds
            .AsNoTracking()
            .Where(build => build.WorkItemId == workItemId)
            .OrderByDescending(build => build.StartedAt)
            .Take(20)
            .Select(build => new BuildRow(
                build.Name,
                build.Sha,
                build.Branch,
                build.Outcome,
                build.StartedAt,
                build.FinishedAt,
                build.Url))
            .ToListAsync(cancellationToken);

        var deployments = await database.Deployments
            .AsNoTracking()
            .Where(one => one.WorkItemId == workItemId)
            .OrderByDescending(one => one.At)
            .Take(20)
            .Select(one => new DeploymentRow(
                one.Environment,
                one.EnvironmentName,
                one.Sha,
                one.Branch,
                one.DeployedBy,
                one.State,
                one.At,
                one.Url))
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
                row.Approved && !row.Blocked))])
        {
            Builds = builds,
            Deployments = deployments,
        };
    }

    /// <summary>
    /// What is running where.
    /// </summary>
    /// <remarks>
    /// Section 13 asked for environments and this is the page that answers it. Until now the
    /// record of what had reached production existed only inside whichever task the work
    /// happened to be attached to, so the question "what is live" could only be answered by
    /// opening work items one at a time and hoping none had been missed.
    ///
    /// Bounded per environment rather than read whole. This page is opened during a release,
    /// and deployments grows by a row per release for ever; what is lost is a repository whose
    /// last release to an environment was more than <see cref="PerEnvironment"/> releases to
    /// that same environment ago, which for a firm this size is years. The total is kept
    /// beside the list so the page can say what it is not showing rather than implying the
    /// list is everything.
    ///
    /// Only the ones that landed. A failed deploy is not what is running, and a page about
    /// what is live that counted attempts would be answering a different question in the same
    /// words.
    /// </remarks>
    public async Task<List<LiveIn>> EnvironmentsAsync(
        CancellationToken cancellationToken = default)
    {
        var totals = await database.Deployments
            .AsNoTracking()
            .Where(one => one.State == DeploymentState.Succeeded)
            .GroupBy(one => one.Environment)
            .Select(group => new { Environment = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Environment, row => row.Count, cancellationToken);

        var live = new List<LiveIn>();

        foreach (var environment in Enum.GetValues<DeploymentEnvironment>())
        {
            var latest = await database.Deployments
                .AsNoTracking()
                .Where(one => one.Environment == environment
                    && one.State == DeploymentState.Succeeded)
                .OrderByDescending(one => one.At)
                .Take(PerEnvironment)
                .Select(one => new DeploymentRow(
                    one.Environment,
                    one.EnvironmentName,
                    one.Sha,
                    one.Branch,
                    one.DeployedBy,
                    one.State,
                    one.At,
                    one.Url))
                .ToListAsync(cancellationToken);

            live.Add(new LiveIn(environment, latest, totals.GetValueOrDefault(environment)));
        }

        return live;
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

/// <summary>What is running in one environment, and how it got there.</summary>
public sealed record LiveIn(
    DeploymentEnvironment Environment,
    IReadOnlyList<DeploymentRow> Latest,
    int Total)
{
    public bool IsEmpty => Latest.Count == 0;
}

/// <summary>One build, as a screen shows it.</summary>
/// <remarks>
/// Carries <c>Name</c> because a repository has several pipelines and "the build failed" is
/// not actionable until somebody knows which of them. Carries <c>Url</c> because the log is
/// on the host and this system deliberately does not keep a copy — so the link is the whole
/// of what a person does next.
/// </remarks>
public sealed record BuildRow(
    string Name,
    string Sha,
    string Branch,
    BuildOutcome Outcome,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? Url)
{
    /// <summary>The short hash, for reading.</summary>
    public string Short => Sha.Length > 7 ? Sha[..7] : Sha;

    /// <summary>
    /// How long it took, said the way somebody says it.
    /// </summary>
    /// <remarks>
    /// Here rather than on the screen because two screens show builds and they have to agree
    /// character for character. Two copies of this expression would differ the first time one
    /// was adjusted, and a reader holding both would conclude one of them was wrong.
    /// </remarks>
    public string? Took
    {
        get
        {
            if (FinishedAt is not { } finished)
            {
                return null;
            }

            var span = finished - StartedAt;

            return span.TotalMinutes < 1
                ? $"{(int)span.TotalSeconds}s"
                : $"{(int)span.TotalMinutes}m {span.Seconds}s";
        }
    }
}

/// <summary>One deployment, as a screen shows it.</summary>
public sealed record DeploymentRow(
    DeploymentEnvironment Environment,
    string EnvironmentName,
    string Sha,
    string? Branch,
    string? DeployedBy,
    DeploymentState State,
    DateTimeOffset At,
    string? Url)
{
    public string Short => Sha.Length > 7 ? Sha[..7] : Sha;

    /// <summary>
    /// Whether the name the host used says more than the classification does.
    /// </summary>
    /// <remarks>
    /// So a screen can show "production" once rather than "Production (production)". A firm
    /// deploying to prod-eu needs to see prod-eu; one deploying to production does not need
    /// to be told twice.
    /// </remarks>
    public bool NameAddsSomething =>
        !string.Equals(EnvironmentName, Environment.ToString(), StringComparison.OrdinalIgnoreCase);
}

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

    /// <summary>
    /// What went out on this day that this person's work was part of.
    /// </summary>
    /// <remarks>
    /// Section 21's last gap. An init property rather than a constructor parameter on purpose:
    /// DayOfWorkAsync returns an empty DayOfWork positionally for somebody with no claimed
    /// handle, and that early return must keep meaning "nothing is known about this person"
    /// rather than quietly acquiring a populated list.
    /// </remarks>
    public IReadOnlyList<DeploymentRow> Deployments { get; init; } = [];

    /// <remarks>
    /// Deployments are counted here as well, and forgetting to would have been the fault
    /// worth catching: a day spent getting a release out, with the commits made the day
    /// before, would have rendered as a blank panel — in the one case the feature was
    /// built for.
    /// </remarks>
    public bool IsEmpty => Commits.Count == 0 && Merged.Count == 0 && Deployments.Count == 0;

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
    /// <summary>What was built, newest first.</summary>
    /// <remarks>
    /// Section 12's first missing link. Init properties rather than constructor parameters so
    /// the empty evidence a screen starts from stays a two-argument expression — the same
    /// reasoning as DayOfWork.Deployments.
    /// </remarks>
    public IReadOnlyList<BuildRow> Builds { get; init; } = [];

    /// <summary>Where it got to, newest first.</summary>
    public IReadOnlyList<DeploymentRow> Deployments { get; init; } = [];

    public bool IsEmpty => Commits.Count == 0
        && PullRequests.Count == 0
        && Builds.Count == 0
        && Deployments.Count == 0;

    /// <summary>
    /// The state of the most recent build, if anything has been built.
    /// </summary>
    /// <remarks>
    /// The most recent and not a summary of all of them, because a task with a red build from
    /// Tuesday and a green one from Thursday is green. Rolling them together would report the
    /// worst thing that ever happened to the branch rather than where it stands.
    /// </remarks>
    public BuildRow? LatestBuild => Builds.Count > 0 ? Builds[0] : null;

    /// <summary>The furthest environment this work actually reached.</summary>
    /// <remarks>
    /// Furthest rather than latest, and only counting the ones that landed. A failed
    /// production deploy after a successful staging one means the work is on staging; saying
    /// "production" because that was the last thing attempted would be the most consequential
    /// wrong sentence on the page.
    /// </remarks>
    public DeploymentRow? Furthest => Deployments
        .Where(one => one.State == DeploymentState.Succeeded)
        .OrderByDescending(one => Rank(one.Environment))
        .FirstOrDefault();

    /// <summary>
    /// How far through the firm's environments one of them is.
    /// </summary>
    /// <remarks>
    /// Written out rather than taken from the enum's own order, which is the fault this
    /// method exists to fix. DeploymentEnvironment.Other is 4 and Production is 3, so
    /// ordering by the enum reported a succeeded deploy to a sandbox or a review app as
    /// further than production — and the summary line on the work item page would have said
    /// "it has reached somewhere else (review-app-17)" about work that was actually live.
    /// The property directly above this one argues about not overstating how far work got,
    /// and it was doing the opposite.
    ///
    /// Other ranks below all three rather than above them, because it means "we could not
    /// tell", and an unknown environment is not evidence of progress. The enum's numbers
    /// cannot simply be reordered: they are persisted as integers and rows already carry
    /// them.
    /// </remarks>
    private static int Rank(DeploymentEnvironment environment) => environment switch
    {
        DeploymentEnvironment.Production => 3,
        DeploymentEnvironment.Staging => 2,
        DeploymentEnvironment.Development => 1,
        _ => 0,
    };
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
