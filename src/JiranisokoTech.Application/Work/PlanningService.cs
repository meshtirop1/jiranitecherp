using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.Work;

namespace JiranisokoTech.Application.Work;

/// <summary>What sprints, the backlog and the links between work need from storage.</summary>
public interface IPlanningRepository
{
    Task<Sprint?> FindSprintAsync(Guid id, CancellationToken cancellationToken = default);

    Task<List<Sprint>> SprintsAsync(CancellationToken cancellationToken = default);

    /// <summary>The one that is running, if any.</summary>
    Task<Sprint?> RunningAsync(CancellationToken cancellationToken = default);

    Task<List<WorkItem>> InSprintAsync(Guid sprintId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Work in no sprint and not finished.
    /// </summary>
    /// <remarks>
    /// Finished work is left out because the backlog answers "what have we not done yet", and a
    /// backlog that grows for ever with completed cards is one nobody opens twice.
    /// </remarks>
    Task<List<WorkItem>> BacklogAsync(CancellationToken cancellationToken = default);

    Task<List<WorkItem>> ChildrenOfAsync(Guid parentId, CancellationToken cancellationToken = default);

    /// <summary>Every parent link in the system, for walking a chain without a query per step.</summary>
    Task<Dictionary<Guid, Guid?>> ParentsAsync(CancellationToken cancellationToken = default);

    Task<List<WorkItemLink>> LinksForAsync(Guid workItemId, CancellationToken cancellationToken = default);

    /// <summary>Every link, for the loop check.</summary>
    Task<List<WorkItemLink>> AllLinksAsync(CancellationToken cancellationToken = default);

    Task<List<WorkItem>> ByIdsAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default);

    void Add(Sprint sprint);

    void Add(WorkItemLink link);

    void Remove(WorkItemLink link);

    Task SaveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Sprints, the backlog, the hierarchy and the dependencies between pieces of work.
/// </summary>
/// <remarks>
/// Section 11's two remaining rows. Everything here is a rule that needs more than one row, which
/// is why it is a service and not more methods on <see cref="WorkItem"/>: whether a parent is
/// really an ancestor, whether another sprint is already running, and whether the thing blocking
/// this is finished are all questions about other rows.
/// </remarks>
public sealed class PlanningService(
    IPlanningRepository planning, IWorkRepository work, IClock clock)
{
    public async Task<Sprint> PlanAsync(
        string name,
        DateOnly starts,
        DateOnly ends,
        string? goal = null,
        CancellationToken cancellationToken = default)
    {
        var sprint = Sprint.Plan(name, starts, ends, goal);

        planning.Add(sprint);
        await planning.SaveAsync(cancellationToken);

        return sprint;
    }

    public async Task DescribeSprintAsync(
        Guid id,
        string name,
        string? goal,
        DateOnly starts,
        DateOnly ends,
        CancellationToken cancellationToken = default)
    {
        var sprint = await RequiredSprint(id, cancellationToken);

        sprint.Describe(name, goal, starts, ends);

        await planning.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Start a sprint.
    /// </summary>
    /// <remarks>
    /// One at a time, and the refusal names the other one. "The current sprint" is a phrase this
    /// system has to be able to answer, and with two running it has no answer — every burndown,
    /// every board and every "what are we doing this week" would have to ask which.
    /// </remarks>
    public async Task StartSprintAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var sprint = await RequiredSprint(id, cancellationToken);

        if (await planning.RunningAsync(cancellationToken) is { } already && already.Id != id)
        {
            throw new InvalidOperationException(
                $"{already.Name} is already running. Finish it first — with two sprints running "
                + "there is no answer to what the firm is working on this week.");
        }

        sprint.Start(clock.Now);

        await planning.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Finish a sprint, sending what did not get done back to the backlog.
    /// </summary>
    /// <remarks>
    /// Back to the backlog rather than into the next sprint, deliberately. Carrying work forward
    /// automatically is how a sprint fills up before anybody has decided what is in it, and the
    /// decision to do a thing next is a decision somebody should make rather than inherit.
    ///
    /// The count is recorded on the sprint because it is a fact about a moment: counting open
    /// items later gives a different answer every time somebody finishes one afterwards, which
    /// would quietly improve the history of every sprint the firm has ever run.
    /// </remarks>
    public async Task<int> FinishSprintAsync(
        Guid id, CancellationToken cancellationToken = default)
    {
        var sprint = await RequiredSprint(id, cancellationToken);
        var items = await planning.InSprintAsync(id, cancellationToken);
        var unfinished = items.Where(one => one.IsOpen).ToList();

        foreach (var item in unfinished)
        {
            item.In(null);
        }

        sprint.Finish(unfinished.Count, clock.Now);

        await planning.SaveAsync(cancellationToken);

        return unfinished.Count;
    }

    /// <summary>Move work into a sprint, or back onto the backlog.</summary>
    public async Task ScheduleAsync(
        Guid workItemId, Guid? sprintId, CancellationToken cancellationToken = default)
    {
        var item = await RequiredItem(workItemId, cancellationToken);

        if (sprintId is { } id)
        {
            var sprint = await RequiredSprint(id, cancellationToken);

            if (!sprint.Accepts)
            {
                throw new InvalidOperationException(
                    $"{sprint.Name} is over. Put it in a sprint that has not finished, or leave "
                    + "it on the backlog.");
            }

            if (!item.IsOpen)
            {
                throw new InvalidOperationException(
                    $"{item.Reference} is {item.Status.ToString().ToLowerInvariant()}. Putting "
                    + "finished work into a sprint would make the sprint report on work it did "
                    + "not do.");
            }
        }

        item.In(sprintId);

        await planning.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Put one piece of work under another.
    /// </summary>
    /// <remarks>
    /// Two checks live here because they need other rows: the parent has to exist, and it must
    /// not already sit somewhere below this item. A loop makes every roll-up run for ever and
    /// every breadcrumb a circle, and it is made by two ordinary moves neither of which looks
    /// wrong on its own.
    /// </remarks>
    public async Task ParentAsync(
        Guid workItemId, Guid? parentId, CancellationToken cancellationToken = default)
    {
        var item = await RequiredItem(workItemId, cancellationToken);

        if (parentId is not { } wanted)
        {
            item.Under(null, null);
            await planning.SaveAsync(cancellationToken);

            return;
        }

        /*
         * Checked here as well as in the aggregate, because the loop walk below would otherwise
         * get there first and say "#1 already sits under #1", which is true and useless.
         */
        if (wanted == workItemId)
        {
            throw new InvalidOperationException("A piece of work cannot sit under itself.");
        }

        var parent = await RequiredItem(wanted, cancellationToken);

        if (await WouldLoop(workItemId, wanted, cancellationToken))
        {
            throw new InvalidOperationException(
                $"{parent.Reference} already sits under {item.Reference}. Putting this one "
                + "under it would make a circle, and nothing that walks the tree could finish.");
        }

        item.Under(wanted, parent.Kind);

        await planning.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Change how big something is.
    /// </summary>
    /// <remarks>
    /// Checked against the parent it already has, because the mistake somebody makes is
    /// promoting a task to an epic while it still sits under a story.
    /// </remarks>
    public async Task KindAsync(
        Guid workItemId, WorkItemKind kind, CancellationToken cancellationToken = default)
    {
        var item = await RequiredItem(workItemId, cancellationToken);

        var parentKind = item.ParentId is { } parent
            ? (await work.FindAsync(parent, cancellationToken))?.Kind
            : null;

        item.IsA(kind, parentKind);

        await planning.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Record that one piece of work has to finish before another can.
    /// </summary>
    /// <remarks>
    /// Refused if it would make a circle, for the same reason the hierarchy is: "what can I start
    /// now" is a question with no answer when A waits for B and B waits for A, and the pair looks
    /// perfectly reasonable one link at a time.
    /// </remarks>
    public async Task BlocksAsync(
        Guid blockerId, Guid blockedId, CancellationToken cancellationToken = default)
    {
        var blocker = await RequiredItem(blockerId, cancellationToken);
        var blocked = await RequiredItem(blockedId, cancellationToken);

        var links = await planning.AllLinksAsync(cancellationToken);

        if (links.Any(one => one.BlockerId == blockerId && one.BlockedId == blockedId))
        {
            return;
        }

        /*
         * Does the blocker already wait on the blocked one, at any depth? That is the question,
         * and the first version of this asked it backwards — Reaches(blockedId, blockerId) —
         * which walks from an item nothing blocks and therefore always answers no. It let a
         * three-card circle straight through, and the test that closed one caught it.
         */
        if (Reaches(links, blockerId, blockedId))
        {
            throw new InvalidOperationException(
                $"{blocker.Reference} already waits on {blocked.Reference}, directly or through "
                + "something else. Adding this would make a circle nothing could ever start.");
        }

        planning.Add(WorkItemLink.Blocks(blockerId, blockedId, clock.Now));

        await planning.SaveAsync(cancellationToken);
    }

    public async Task UnblockAsync(
        Guid blockerId, Guid blockedId, CancellationToken cancellationToken = default)
    {
        var links = await planning.AllLinksAsync(cancellationToken);
        var link = links.FirstOrDefault(
            one => one.BlockerId == blockerId && one.BlockedId == blockedId);

        if (link is null)
        {
            return;
        }

        planning.Remove(link);

        await planning.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// What is still in this one's way.
    /// </summary>
    /// <remarks>
    /// Unfinished blockers only, because a finished one is not in the way and listing it would
    /// make every long-lived card look permanently stuck.
    /// </remarks>
    public async Task<List<WorkItem>> WaitingOnAsync(
        Guid workItemId, CancellationToken cancellationToken = default)
    {
        var links = await planning.LinksForAsync(workItemId, cancellationToken);
        var blockerIds = links
            .Where(one => one.BlockedId == workItemId)
            .Select(one => one.BlockerId)
            .ToList();

        return blockerIds.Count == 0
            ? []
            : [.. (await planning.ByIdsAsync(blockerIds, cancellationToken))
                .Where(one => one.IsOpen)];
    }

    /// <summary>What this one is holding up.</summary>
    public async Task<List<WorkItem>> HoldingUpAsync(
        Guid workItemId, CancellationToken cancellationToken = default)
    {
        var links = await planning.LinksForAsync(workItemId, cancellationToken);
        var blockedIds = links
            .Where(one => one.BlockerId == workItemId)
            .Select(one => one.BlockedId)
            .ToList();

        return blockedIds.Count == 0
            ? []
            : await planning.ByIdsAsync(blockedIds, cancellationToken);
    }

    public Task<List<Sprint>> SprintsAsync(CancellationToken cancellationToken = default) =>
        planning.SprintsAsync(cancellationToken);

    public Task<Sprint?> SprintAsync(Guid id, CancellationToken cancellationToken = default) =>
        planning.FindSprintAsync(id, cancellationToken);

    public Task<Sprint?> RunningAsync(CancellationToken cancellationToken = default) =>
        planning.RunningAsync(cancellationToken);

    public Task<List<WorkItem>> InSprintAsync(
        Guid sprintId, CancellationToken cancellationToken = default) =>
        planning.InSprintAsync(sprintId, cancellationToken);

    public Task<List<WorkItem>> BacklogAsync(CancellationToken cancellationToken = default) =>
        planning.BacklogAsync(cancellationToken);

    public Task<List<WorkItem>> ChildrenOfAsync(
        Guid parentId, CancellationToken cancellationToken = default) =>
        planning.ChildrenOfAsync(parentId, cancellationToken);

    /// <summary>
    /// Whether making <paramref name="wanted"/> the parent of <paramref name="workItemId"/> would
    /// close a circle.
    /// </summary>
    /// <remarks>
    /// Walked over the whole set of parent links read in one query, the same decision the
    /// reporting lines make: this is a board of hundreds of cards, not millions, and a round trip
    /// per level is a query count that depends on how deeply somebody has nested their epics.
    /// </remarks>
    private async Task<bool> WouldLoop(
        Guid workItemId, Guid wanted, CancellationToken cancellationToken)
    {
        var parents = await planning.ParentsAsync(cancellationToken);
        var seen = new HashSet<Guid>();
        var at = (Guid?)wanted;

        while (at is { } step && seen.Add(step))
        {
            if (step == workItemId)
            {
                return true;
            }

            at = parents.GetValueOrDefault(step);
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="from"/> already waits on <paramref name="target"/>, at any depth.
    /// </summary>
    /// <remarks>
    /// A breadth-first walk over the links in hand. The visited set is what stops it running for
    /// ever over a circle that already exists — and one might, because a link could have been
    /// written before this check did, and a guard that assumes the data is clean is a guard that
    /// hangs the first time it is wrong.
    /// </remarks>
    private static bool Reaches(
        IReadOnlyCollection<WorkItemLink> links, Guid from, Guid target)
    {
        var seen = new HashSet<Guid> { from };
        var queue = new Queue<Guid>();

        queue.Enqueue(from);

        while (queue.Count > 0)
        {
            var at = queue.Dequeue();

            if (at == target)
            {
                return true;
            }

            foreach (var next in links.Where(one => one.BlockedId == at).Select(one => one.BlockerId))
            {
                if (seen.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        return false;
    }

    private async Task<Sprint> RequiredSprint(Guid id, CancellationToken cancellationToken) =>
        await planning.FindSprintAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("That sprint does not exist.");

    private async Task<WorkItem> RequiredItem(Guid id, CancellationToken cancellationToken) =>
        await work.FindAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("That work is not on file.");
}
