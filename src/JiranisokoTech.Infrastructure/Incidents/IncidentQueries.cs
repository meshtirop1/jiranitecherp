using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Domain.Incidents;
using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Incidents;

/// <summary>One incident, as a list shows it.</summary>
public sealed record IncidentRow(
    Guid Id,
    int Number,
    string Title,
    IncidentSeverity Severity,
    IncidentStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset ReportedAt,
    DateTimeOffset? MitigatedAt,
    DateTimeOffset? ResolvedAt,
    string? Affects,
    Guid? LeadId,
    bool HasReview,
    bool ReviewAgreed)
{
    public bool IsOpen => Status == IncidentStatus.Open;

    /// <summary>How long this has been going, or how long it went on for.</summary>
    public TimeSpan Ran(DateTimeOffset now) => (ResolvedAt ?? now) - StartedAt;

    /// <summary>
    /// A resolved incident nobody has reviewed.
    /// </summary>
    /// <remarks>
    /// Shown rather than enforced, and only for the two severities where it matters. Refusing to
    /// close an incident without a review would produce reviews written to close incidents,
    /// which are worse than none; a list that quietly says "three of these were never looked at"
    /// is a thing somebody can act on or decide not to.
    /// </remarks>
    public bool ReviewMissing =>
        Status == IncidentStatus.Resolved
        && !HasReview
        && Severity != IncidentSeverity.Minor;
}

/// <summary>
/// Something that went out just before an incident started.
/// </summary>
/// <remarks>
/// The first question anybody asks in an incident, answered without anybody typing. This system
/// already records deployments from four hosts and releases the firm declared itself, so "what
/// changed" is a query rather than a conversation.
/// </remarks>
public sealed record Suspect(
    string What, string Where, string Sha, DateTimeOffset At, string? Who, bool IsRelease);

/// <summary>Where a corrective action has got to on the board.</summary>
public sealed record ActionState(Guid WorkItemId, int Number, string Title, WorkItemStatus Status)
{
    public bool IsDone => WorkItem.Finished.Contains(Status);
}

/// <summary>
/// What the incident screens read.
/// </summary>
/// <remarks>
/// Separate from the repository because none of this is an aggregate: a list of incidents with
/// whether each has a review is a projection across two tables, and loading the aggregates to
/// build it would read every timeline line in the firm's history to draw one page.
/// </remarks>
public sealed class IncidentQueries(AppDbContext database)
{
    /// <summary>The most incidents one page shows.</summary>
    /// <remarks>
    /// Open ones are never cut — there are never many, and one dropped off a list is the whole
    /// failure mode of this page. The bound is on what is over.
    /// </remarks>
    public const int MostShown = 100;

    /// <summary>
    /// Everything happening, then what recently was.
    /// </summary>
    /// <remarks>
    /// Open first, worst first, oldest first within a severity — which is the order somebody
    /// opening this page in an emergency needs, rather than the order things were typed.
    /// </remarks>
    public async Task<List<IncidentRow>> AllAsync(CancellationToken cancellationToken = default)
    {
        var rows = await database.Incidents
            .AsNoTracking()
            .OrderByDescending(one => one.StartedAt)
            .Take(MostShown)
            .Select(one => new
            {
                one.Id,
                one.Number,
                one.Title,
                one.Severity,
                one.Status,
                one.StartedAt,
                one.ReportedAt,
                one.MitigatedAt,
                one.ResolvedAt,
                one.Affects,
                one.LeadId,
            })
            .ToListAsync(cancellationToken);

        var ids = rows.Select(one => one.Id).ToList();

        var reviews = await database.Postmortems
            .AsNoTracking()
            .Where(one => ids.Contains(one.IncidentId))
            .Select(one => new { one.IncidentId, one.Status })
            .ToDictionaryAsync(one => one.IncidentId, one => one.Status, cancellationToken);

        return
        [
            .. rows
                .Select(one => new IncidentRow(
                    one.Id,
                    one.Number,
                    one.Title,
                    one.Severity,
                    one.Status,
                    one.StartedAt,
                    one.ReportedAt,
                    one.MitigatedAt,
                    one.ResolvedAt,
                    one.Affects,
                    one.LeadId,
                    reviews.ContainsKey(one.Id),
                    reviews.GetValueOrDefault(one.Id) == PostmortemStatus.Agreed))
                .OrderBy(one => one.IsOpen ? 0 : 1)
                .ThenBy(one => one.IsOpen ? (int)one.Severity : 0)
                .ThenByDescending(one => one.StartedAt)
        ];
    }

    /// <summary>How many are happening right now.</summary>
    /// <remarks>
    /// For the navigation, because an open incident is the one thing in this application that
    /// should be visible from every page. Counting mitigated ones too would be wrong — the harm
    /// has stopped, and a number in the navigation that never clears trains people to ignore it.
    /// </remarks>
    public Task<int> OpenAsync(CancellationToken cancellationToken = default) =>
        database.Incidents.CountAsync(
            one => one.Status == IncidentStatus.Open, cancellationToken);

    /// <summary>
    /// What went out shortly before this started.
    /// </summary>
    /// <remarks>
    /// Deployments that succeeded and releases that were declared, in a window before the
    /// incident began. <b>Named suspects rather than causes</b>, and the screen repeats the word:
    /// most incidents do follow a change and plenty do not, and a heading saying "caused by"
    /// would have somebody stop looking at the moment they should start.
    ///
    /// The window ends at the start rather than at the report, because a deployment that
    /// happened after the thing was already broken did not break it — though it is exactly the
    /// sort of coincidence people convince themselves of at two in the morning.
    /// </remarks>
    public async Task<List<Suspect>> SuspectsAsync(
        DateTimeOffset startedAt,
        TimeSpan window,
        CancellationToken cancellationToken = default)
    {
        var from = startedAt - window;

        var deployments = await database.Deployments
            .AsNoTracking()
            .Where(one => one.State == DeploymentState.Succeeded
                && one.At >= from
                && one.At <= startedAt)
            .OrderByDescending(one => one.At)
            .Select(one => new Suspect(
                "Deployed",
                one.EnvironmentName,
                one.Sha,
                one.At,
                one.DeployedBy,
                false))
            .ToListAsync(cancellationToken);

        var releases = await database.Releases
            .AsNoTracking()
            .Where(one => one.DeclaredAt != null
                && one.DeclaredAt >= from
                && one.DeclaredAt <= startedAt)
            .OrderByDescending(one => one.DeclaredAt)
            .Select(one => new Suspect(
                "Released " + one.Number,
                "declared",
                one.Sha,
                one.DeclaredAt!.Value,
                null,
                true))
            .ToListAsync(cancellationToken);

        return [.. deployments.Concat(releases).OrderByDescending(one => one.At)];
    }

    /// <summary>
    /// Where each corrective action has got to.
    /// </summary>
    /// <remarks>
    /// Read from the board rather than stored on the review, which is the point of putting them
    /// on the board at all. A review that recorded its own idea of whether an action was done
    /// would be a second copy of the truth, and the one nobody updates.
    /// </remarks>
    public async Task<List<ActionState>> ActionStatesAsync(
        IReadOnlyCollection<Guid> workItemIds, CancellationToken cancellationToken = default)
    {
        if (workItemIds.Count == 0)
        {
            return [];
        }

        return await database.WorkItems
            .AsNoTracking()
            .Where(one => workItemIds.Contains(one.Id))
            .Select(one => new ActionState(one.Id, one.Number, one.Title, one.Status))
            .ToListAsync(cancellationToken);
    }
}
