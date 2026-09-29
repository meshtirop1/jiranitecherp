using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Ai;
using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.Authorization;
using JiranisokoTech.Infrastructure.Business;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Infrastructure.Reporting;
using JiranisokoTech.Infrastructure.Work;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Ai;

/// <summary>
/// What is recorded about one project, gathered as one person and no further than they may see.
/// </summary>
/// <remarks>
/// Section 37 asks for a project's status, risks, blockers, deadlines, workload, open issues,
/// pull requests, deployments and money, and then asks that actual data, calculated metrics and
/// inference be told apart. This class is the first two. Everything here is either read from a
/// record or computed from what was read by code anybody can check; the model's reading is laid
/// on top of it by <see cref="ProjectReading"/>, and never mixed into it.
///
/// Each part is gathered only if the person holds what the page showing the same records asks
/// for, and the parts left out are named, with the reason, in <see cref="ProjectPicture.Withheld"/>.
/// Naming them matters twice over: the person sees why the reading is thinner than they expected,
/// and the model is told that the absence of builds means "not shown to this person" rather than
/// "no builds", which it would otherwise read as good news.
/// </remarks>
public sealed class ProjectFacts(
    AppDbContext database,
    Reaches reaches,
    WorkQueries work,
    BusinessQueries business,
    ProjectMoneyQueries projectMoney,
    IClock clock)
{
    /// <summary>How far back builds, deployments and hours are read.</summary>
    public const int WindowDays = 14;

    /// <summary>
    /// The most work items sent to a model for one project, most pressing first.
    /// </summary>
    /// <remarks>
    /// The page shows them all. A model is sent a bounded number because a long-running project
    /// can hold thousands, and a request that large is slow, costly and — past a point — refused;
    /// the calculated figures, which are counted over every item, carry the whole picture.
    /// </remarks>
    public const int MostWorkSent = 150;

    /// <summary>The projects this person may ask about — the same reach their search box uses.</summary>
    public async Task<List<ProjectRow>> WithinReachAsync(
        Asker asker, CancellationToken cancellationToken = default)
    {
        var reach = await reaches.ProjectsAsync(asker.Permissions, asker.EmployeeId, cancellationToken);

        if (reach.IsNothing)
        {
            return [];
        }

        return [.. (await work.ProjectsAsync(cancellationToken: cancellationToken))
            .Where(project => reach.Includes(project.Id))];
    }

    /// <summary>
    /// Everything this person may see about one project, or null when it is not theirs to see.
    /// </summary>
    /// <remarks>
    /// Null for a project out of reach and for one that does not exist alike. A caller that could
    /// tell the two apart could learn which project identifiers exist by asking about them.
    /// </remarks>
    public async Task<ProjectPicture?> GatherAsync(
        Guid projectId, Asker asker, CancellationToken cancellationToken = default)
    {
        var reach = await reaches.ProjectsAsync(asker.Permissions, asker.EmployeeId, cancellationToken);

        if (!reach.Includes(projectId))
        {
            return null;
        }

        var project = (await work.ProjectsAsync(cancellationToken: cancellationToken))
            .FirstOrDefault(one => one.Id == projectId);

        if (project is null)
        {
            return null;
        }

        var today = clock.Today;
        var since = clock.Now.AddDays(-WindowDays);
        var withheld = new List<string>();

        /*
         * The work, all of it for somebody who may see the whole board and only their own for
         * somebody who may not — the rule the board itself applies. A member of a project holding
         * tasks.view_own sees the project because they work on it; that is not a reason to show
         * them what everybody else on it is doing.
         */
        var everyItem = await work.ItemsAsync(projectId: projectId, cancellationToken: cancellationToken);
        var seesAllWork = asker.Holds(Permissions.TasksViewAll) || asker.Holds(Permissions.ProjectsViewAll);

        var items = seesAllWork
            ? everyItem
            : [.. everyItem.Where(item => item.AssigneeId is { } who && who == asker.EmployeeId)];

        if (!seesAllWork)
        {
            withheld.Add("Work assigned to other people: you may see only your own work.");
        }

        List<PullRequestFact>? pulls = null;
        List<BuildFact>? builds = null;
        List<DeploymentFact>? deployments = null;

        if (asker.Holds(Permissions.ReposView))
        {
            var repositories = await database.Repositories
                .AsNoTracking()
                .Where(repository => repository.ProjectId == projectId)
                .Select(repository => new { repository.Id, Name = repository.Owner + "/" + repository.Name })
                .ToListAsync(cancellationToken);

            var ids = repositories.Select(repository => repository.Id).ToList();
            var names = repositories.ToDictionary(repository => repository.Id, repository => repository.Name);

            pulls = [.. (await database.PullRequests
                .AsNoTracking()
                .Where(pull => ids.Contains(pull.RepositoryId) && pull.State == PullRequestState.Open)
                .OrderBy(pull => pull.OpenedAt)
                .Take(30)
                .Select(pull => new { pull.RepositoryId, pull.Number, pull.Title, pull.Author, pull.OpenedAt })
                .ToListAsync(cancellationToken))
                .Select(pull => new PullRequestFact(
                    names[pull.RepositoryId], pull.Number, pull.Title, pull.Author, pull.OpenedAt))];

            builds = [.. (await database.Builds
                .AsNoTracking()
                .Where(build => ids.Contains(build.RepositoryId) && build.StartedAt >= since)
                .OrderByDescending(build => build.StartedAt)
                .Take(40)
                .Select(build => new { build.RepositoryId, build.Name, build.Branch, build.Outcome, build.StartedAt, build.Url })
                .ToListAsync(cancellationToken))
                .Select(build => new BuildFact(
                    names[build.RepositoryId], build.Name, build.Branch, build.Outcome, build.StartedAt, build.Url))];

            deployments = [.. (await database.Deployments
                .AsNoTracking()
                .Where(deployment => ids.Contains(deployment.RepositoryId) && deployment.At >= since)
                .OrderByDescending(deployment => deployment.At)
                .Take(40)
                .Select(deployment => new { deployment.RepositoryId, deployment.EnvironmentName, deployment.State, deployment.At, deployment.Url })
                .ToListAsync(cancellationToken))
                .Select(deployment => new DeploymentFact(
                    names[deployment.RepositoryId], deployment.EnvironmentName, deployment.State, deployment.At, deployment.Url))];

            if (repositories.Count == 0)
            {
                withheld.Add("Pull requests, builds and deployments: no repository is connected to this project.");
            }
        }
        else
        {
            withheld.Add("Pull requests, builds and deployments: you do not hold repos.view.");
        }

        HoursFact? hours = null;
        MoneyFact? money = null;

        /*
         * Hours and money behind projects.view_all, which is what the project page and the money
         * page both ask for. A member of the project does not see what it has cost, and neither
         * does the model when they are the one asking.
         */
        if (asker.Holds(Permissions.ProjectsViewAll))
        {
            var time = await business.TimeAsync(projectId: projectId, cancellationToken: cancellationToken);
            var windowStart = today.AddDays(-WindowDays);

            hours = new HoursFact(
                time.Sum(entry => entry.Minutes),
                time.Where(entry => entry.On >= windowStart).Sum(entry => entry.Minutes),
                time.Where(entry => entry.IsApproved && entry.IsBillable && !entry.IsInvoiced)
                    .Sum(entry => entry.Minutes));

            var figures = (await projectMoney.AllAsync(cancellationToken))
                .FirstOrDefault(one => one.Id == projectId);

            if (figures is not null)
            {
                money = new MoneyFact(
                    figures.Budget?.ToString(),
                    figures.Invoiced?.ToString(),
                    figures.Cost?.ToString(),
                    figures.Margin?.ToString(),
                    BudgetUsedPercent(figures));
            }
        }
        else
        {
            withheld.Add("Hours and money: you do not hold projects.view_all.");
        }

        return new ProjectPicture(
            project,
            today,
            [.. items.Select(item => new WorkFact(
                item.Reference,
                item.Title,
                item.Status,
                item.Priority,
                item.AssigneeName,
                item.DueOn,
                item.EstimateMinutes,
                item.BlockedReason))],
            seesAllWork,
            pulls,
            builds,
            deployments,
            hours,
            money,
            withheld);
    }

    /// <summary>
    /// The share of the budget already spent, when both are known and in one currency.
    /// </summary>
    /// <remarks>
    /// Null rather than a guess when the currencies differ or the cost rate is not set — the money
    /// page refuses to add across currencies and so does this.
    /// </remarks>
    private static int? BudgetUsedPercent(ProjectMoney figures)
    {
        if (figures.Budget is not { } budget || figures.Cost is not { } cost
            || budget.Currency != cost.Currency || budget.MinorUnits <= 0)
        {
            return null;
        }

        return (int)Math.Round(cost.MinorUnits * 100.0 / budget.MinorUnits);
    }
}

/// <summary>Everything recorded about a project that one person may see.</summary>
public sealed record ProjectPicture(
    ProjectRow Project,
    DateOnly Today,
    IReadOnlyList<WorkFact> Work,
    bool SeesAllWork,
    IReadOnlyList<PullRequestFact>? OpenPullRequests,
    IReadOnlyList<BuildFact>? Builds,
    IReadOnlyList<DeploymentFact>? Deployments,
    HoursFact? Hours,
    MoneyFact? Money,
    IReadOnlyList<string> Withheld)
{
    /// <summary>
    /// Figures computed from the facts above, each with how it was computed.
    /// </summary>
    /// <remarks>
    /// Section 37's middle category. Computed here, by code, so that "3 items overdue" on the page
    /// is a count anybody can check against the list beside it — and so the model is handed these
    /// figures rather than asked to count, which it can get wrong while sounding sure.
    /// </remarks>
    public IReadOnlyList<Calculated> Calculations()
    {
        var open = Work.Where(item => !WorkItem.Finished.Contains(item.Status)).ToList();
        var figures = new List<Calculated>
        {
            new("Open work", $"{open.Count} of {Work.Count}",
                "Items not done, deployed or cancelled, out of every item shown."),
            new("Overdue", $"{open.Count(item => item.DueOn < Today)}",
                "Open items whose due date is before today."),
            new("Blocked", $"{open.Count(item => item.Status == WorkItemStatus.Blocked)}",
                "Open items in the blocked state."),
            new("Unassigned", $"{open.Count(item => item.AssignedTo is null)}",
                "Open items nobody holds."),
            new("Estimated work left", Said(open.Sum(item => item.EstimateMinutes ?? 0)),
                $"The sum of estimates on open items; {open.Count(item => item.EstimateMinutes is null)} open items carry no estimate."),
        };

        if (Project.DueOn is { } due)
        {
            var days = due.DayNumber - Today.DayNumber;

            figures.Add(new("Days to the due date",
                days >= 0 ? $"{days}" : $"{-days} past it",
                "The project's due date against today."));
        }

        if (OpenPullRequests is { } pulls)
        {
            figures.Add(new("Open pull requests", $"{pulls.Count}", "Pull requests open in this project's repositories."));

            if (pulls.Count > 0)
            {
                figures.Add(new("Oldest open pull request",
                    $"{(int)(Today.ToDateTime(TimeOnly.MinValue) - pulls.Min(pull => pull.OpenedAt).UtcDateTime).TotalDays} days",
                    "Days since the oldest open pull request was opened."));
            }
        }

        if (Builds is { } builds)
        {
            figures.Add(new($"Failed builds, last {ProjectFacts.WindowDays} days",
                $"{builds.Count(build => build.Outcome == BuildOutcome.Failed)} of {builds.Count}",
                "Builds that failed, out of every build started in the window."));
        }

        if (Deployments is { } deployments)
        {
            figures.Add(new($"Failed deployments, last {ProjectFacts.WindowDays} days",
                $"{deployments.Count(one => one.State == DeploymentState.Failed)} of {deployments.Count}",
                "Deployments that failed, out of every deployment in the window."));
        }

        if (Hours is { } hours)
        {
            figures.Add(new($"Hours logged, last {ProjectFacts.WindowDays} days", Said(hours.LastWindowMinutes),
                "Every time entry against the project in the window, approved or not."));
        }

        if (Money?.BudgetUsedPercent is { } used)
        {
            figures.Add(new("Budget used", $"{used}%", "Cost so far — hours at the firm's standard rate plus paid expenses — against the budget."));
        }

        return figures;
    }

    private static string Said(int minutes) => minutes % 60 == 0
        ? $"{minutes / 60}h"
        : $"{minutes / 60}h {minutes % 60}m";
}

public sealed record WorkFact(
    string Reference,
    string Title,
    WorkItemStatus Status,
    Priority Priority,
    string? AssignedTo,
    DateOnly? DueOn,
    int? EstimateMinutes,
    string? BlockedBecause);

public sealed record PullRequestFact(string Repository, int Number, string Title, string Author, DateTimeOffset OpenedAt);

public sealed record BuildFact(string Repository, string Name, string Branch, BuildOutcome Outcome, DateTimeOffset StartedAt, string? Url);

public sealed record DeploymentFact(string Repository, string Environment, DeploymentState State, DateTimeOffset At, string? Url);

public sealed record HoursFact(int TotalMinutes, int LastWindowMinutes, int ApprovedUnbilledMinutes);

public sealed record MoneyFact(string? Budget, string? Invoiced, string? Cost, string? Margin, int? BudgetUsedPercent);

/// <param name="How">What was counted, in words, so the figure can be checked.</param>
public sealed record Calculated(string Name, string Value, string How);
