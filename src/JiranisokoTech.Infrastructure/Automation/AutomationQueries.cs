using JiranisokoTech.Domain.Automation;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Automation;

/// <summary>One rule on the rules page.</summary>
public sealed record RuleRow(
    Guid Id,
    string Name,
    string Trigger,
    bool IsOn,
    bool IsTemplate,
    int Conditions,
    int Actions,
    int DelayMinutes,
    DateTimeOffset? LastMatchedAt,
    int Runs,
    int Failing);

/// <summary>Reading the rules and their history, for the pages.</summary>
public sealed class AutomationQueries(AppDbContext database)
{
    /// <summary>How much history a page shows.</summary>
    /// <remarks>
    /// Fifty, newest first, and the page says so. A rule that fires every hour has a history of
    /// thousands within a year, and a page that tried to show all of it would be the slowest in
    /// the application while nobody read past the first screen.
    /// </remarks>
    public const int MostRuns = 50;

    public async Task<List<RuleRow>> RulesAsync(CancellationToken cancellationToken = default)
    {
        var rules = await database.AutomationRules
            .AsNoTracking()
            .OrderBy(one => one.Name)
            .ToListAsync(cancellationToken);

        var tallies = await database.AutomationRuns
            .AsNoTracking()
            .GroupBy(one => one.RuleId)
            .Select(group => new
            {
                RuleId = group.Key,
                Runs = group.Count(),
                Failing = group.Count(one => one.Status == AutomationRunStatus.Retrying
                    || one.Status == AutomationRunStatus.GaveUp),
            })
            .ToListAsync(cancellationToken);

        /*
         * One small query per rule rather than a maximum inside the grouping above. The
         * timestamps are text on SQLite, where a grouped MAX over them is not something EF will
         * translate, and there are tens of rules rather than thousands — each of these is a seek
         * on the rule's history index.
         */
        var latest = new Dictionary<Guid, DateTimeOffset>();

        foreach (var rule in rules)
        {
            var last = await database.AutomationRuns
                .AsNoTracking()
                .Where(one => one.RuleId == rule.Id)
                .OrderByDescending(one => one.MatchedAt)
                .Select(one => (DateTimeOffset?)one.MatchedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (last is { } at)
            {
                latest[rule.Id] = at;
            }
        }

        return [.. rules.Select(rule =>
        {
            var tally = tallies.FirstOrDefault(one => one.RuleId == rule.Id);

            return new RuleRow(
                rule.Id,
                rule.Name,
                rule.Trigger,
                rule.IsOn,
                rule.IsTemplate,
                rule.Conditions.Count,
                rule.Actions.Count,
                rule.DelayMinutes,
                latest.TryGetValue(rule.Id, out var at) ? at : null,
                tally?.Runs ?? 0,
                tally?.Failing ?? 0);
        })];
    }

    public Task<AutomationRule?> RuleAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.AutomationRules
            .AsNoTracking()
            .FirstOrDefaultAsync(one => one.Id == id, cancellationToken);

    /// <summary>The newest runs, of one rule or of all of them.</summary>
    public Task<List<AutomationRun>> RunsAsync(
        Guid? ruleId = null, CancellationToken cancellationToken = default) =>
        database.AutomationRuns
            .AsNoTracking()
            .Where(one => ruleId == null || one.RuleId == ruleId)
            .OrderByDescending(one => one.MatchedAt)
            .Take(MostRuns)
            .ToListAsync(cancellationToken);

    /// <summary>Everybody still here, for choosing a named person.</summary>
    public Task<List<(Guid Id, string Name)>> PeopleAsync(
        CancellationToken cancellationToken = default) =>
        database.Employees
            .AsNoTracking()
            .Where(one => one.Status != EmploymentStatus.Left)
            .OrderBy(one => one.FullName)
            .Select(one => new ValueTuple<Guid, string>(one.Id, one.FullName))
            .ToListAsync(cancellationToken);

    /// <summary>Projects still running, which are the only ones work can be raised on.</summary>
    public Task<List<(Guid Id, string Name)>> ProjectsAsync(
        CancellationToken cancellationToken = default) =>
        database.Projects
            .AsNoTracking()
            .Where(one => one.Status == ProjectStatus.Planned
                || one.Status == ProjectStatus.Active
                || one.Status == ProjectStatus.OnHold)
            .OrderBy(one => one.Name)
            .Select(one => new ValueTuple<Guid, string>(one.Id, one.Name))
            .ToListAsync(cancellationToken);

    public Task<List<(Guid Id, string Name)>> SubscriptionsAsync(
        CancellationToken cancellationToken = default) =>
        database.Subscriptions
            .AsNoTracking()
            .OrderBy(one => one.Name)
            .Select(one => new ValueTuple<Guid, string>(one.Id, one.Name))
            .ToListAsync(cancellationToken);
}
