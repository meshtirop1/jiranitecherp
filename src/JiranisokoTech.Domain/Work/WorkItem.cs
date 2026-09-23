using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Work;

/// <summary>
/// One piece of work somebody is answerable for.
/// </summary>
/// <remarks>
/// Called a work item rather than a task, and not for taste: <c>Task</c> is the
/// type every asynchronous method in this codebase returns, and a domain class
/// with that name turns every signature into <c>Task&lt;Task&gt;</c> and every
/// file into an argument about which one is meant.
///
/// The status is a small state machine rather than a settable property. A free
/// column is how work gets marked done without being reviewed, cancelled and
/// then quietly resumed, or blocked with no note of what is blocking it — each
/// of which looks fine in the database and is a lie on the board.
/// </remarks>
public sealed class WorkItem : Entity, IAuditable
{
    /// <summary>
    /// What may follow what.
    /// </summary>
    /// <remarks>
    /// Written out rather than inferred, because the interesting entries are the
    /// ones that are missing. Nothing leaves Cancelled: work that is picked up
    /// again is new work with a new decision behind it, and reviving the old row
    /// silently rewrites why it was dropped.
    /// </remarks>
    private static readonly Dictionary<WorkItemStatus, WorkItemStatus[]> Allowed = new()
    {
        [WorkItemStatus.Todo] =
            [WorkItemStatus.InProgress, WorkItemStatus.Blocked, WorkItemStatus.Cancelled],

        [WorkItemStatus.InProgress] =
            [WorkItemStatus.InReview, WorkItemStatus.Blocked, WorkItemStatus.Todo, WorkItemStatus.Cancelled],

        // Review can send it back, which is the whole point of having the state.
        [WorkItemStatus.InReview] =
            [WorkItemStatus.Done, WorkItemStatus.InProgress, WorkItemStatus.Blocked, WorkItemStatus.Cancelled],

        [WorkItemStatus.Blocked] =
            [WorkItemStatus.Todo, WorkItemStatus.InProgress, WorkItemStatus.Cancelled],

        // Reopening finished work is allowed, because it happens; it goes back to
        // being in progress rather than straight to done again. Releasing it is
        // the other way out, and the only state that leads to a release.
        [WorkItemStatus.Done] = [WorkItemStatus.InProgress, WorkItemStatus.Deployed],

        [WorkItemStatus.Cancelled] = [],

        /*
         * Nothing leaves Deployed, and this is the entry worth arguing about.
         *
         * Every other state is a statement about the firm's own intentions,
         * which may be revised. This one is a statement about the world outside
         * it: the thing is live, clients are using it, and somebody's release
         * note says so. Moving the row back to in progress would leave the
         * system claiming a release never happened while the release is still
         * out there, and the board would be the only party that had been told.
         *
         * So a fault found after a release, or a rollback, is new work with its
         * own row and its own reason — the same rule as cancelled work, arrived
         * at from the opposite direction. The old row keeps standing as the
         * record of what went out and when.
         */
        [WorkItemStatus.Deployed] = [],
    };

    private WorkItem()
    {
        Title = string.Empty;
    }

    private WorkItem(string title, Guid raisedById, Guid? projectId, Priority priority)
    {
        Title = Require(title, nameof(title));
        RaisedById = raisedById;
        ProjectId = projectId;
        Priority = priority;
        Status = WorkItemStatus.Todo;

        Raise(new WorkItemRaised(Id, Title, projectId, raisedById));
    }

    public static WorkItem Raise(
        string title,
        Guid raisedById,
        Guid? projectId = null,
        Priority priority = Priority.Normal) =>
        new(title, raisedById, projectId, priority);

    public string Title { get; private set; }

    public string? Detail { get; private set; }

    public Guid? ProjectId { get; private set; }

    /// <summary>Who asked for it. Never changes; it is a fact about the past.</summary>
    public Guid RaisedById { get; private init; }

    /// <summary>Who is answerable for it. One person, or nobody.</summary>
    /// <remarks>
    /// Deliberately not a list. Work owned by three people is work owned by
    /// nobody, and the board stops being a statement about who is doing what.
    /// </remarks>
    public Guid? AssigneeId { get; private set; }

    public WorkItemStatus Status { get; private set; }

    public Priority Priority { get; private set; }

    /// <summary>
    /// The estimate, in whole minutes.
    /// </summary>
    /// <remarks>
    /// Minutes as an integer for the same reason money is held in minor units:
    /// a decimal number of hours accumulates rounding across a sprint, and
    /// "37.499999 hours" is a figure somebody has to explain.
    /// </remarks>
    public int? EstimateMinutes { get; private set; }

    public DateOnly? DueOn { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Why it is stopped. Set only while blocked, and cleared on the way out.</summary>
    public string? BlockedReason { get; private set; }

    /// <summary>
    /// The states that mean nobody is waiting on this any more.
    /// </summary>
    /// <remarks>
    /// A list rather than a pattern in <see cref="IsOpen"/>, because the same
    /// question is asked in SQL in five places — project counts, the board
    /// filter, the overdue figure, the work a leaver has to hand over — and a
    /// computed property cannot be translated into a query. Each of those places
    /// had its own copy of "not Done and not Cancelled", so adding a seventh
    /// state made a released item count as open work in all five at once, in
    /// every direction quietly: an inflated open count, a released item reported
    /// as overdue, and a leaver's finished work taken off them again. They now
    /// read this, so the answer cannot differ between a query and an object.
    /// </remarks>
    public static IReadOnlyList<WorkItemStatus> Finished { get; } =
        [WorkItemStatus.Done, WorkItemStatus.Cancelled, WorkItemStatus.Deployed];

    public bool IsOpen => !Finished.Contains(Status);

    /// <summary>
    /// Where work in this state may go next.
    /// </summary>
    /// <remarks>
    /// Exposed so a screen can offer only the moves that will be accepted. The
    /// alternative — a copy of the table in the page — is a second source of
    /// truth, and the symptom when it drifts is a button that always refuses.
    /// </remarks>
    public static IReadOnlyList<WorkItemStatus> NextFrom(WorkItemStatus status) =>
        Allowed[status];

    /// <summary>
    /// Move it along.
    /// </summary>
    /// <remarks>
    /// One method rather than one per transition, so the table above is the only
    /// place the rules live. A caller asking for something the table does not
    /// allow gets a message naming both states, because "invalid transition" on
    /// its own sends somebody to read this file.
    /// </remarks>
    public void MoveTo(WorkItemStatus status, DateTimeOffset at, string? because = null)
    {
        if (Status == status)
        {
            return;
        }

        if (!Allowed[Status].Contains(status))
        {
            // The two dead ends get a sentence of their own, because "cannot go
            // from deployed to in progress" reads as a missing feature, and what
            // is actually being said is that the row is a record of something
            // that has already left the firm.
            throw new InvalidOperationException(
                $"Work cannot go from {Describe(Status)} to {Describe(status)}."
                + Status switch
                {
                    WorkItemStatus.Cancelled =>
                        " Cancelled work stays cancelled; raise it again if it is wanted.",
                    WorkItemStatus.Deployed =>
                        " Deployed work stays deployed; a change to something released is new work.",
                    _ => string.Empty,
                });
        }

        if (status == WorkItemStatus.Blocked && string.IsNullOrWhiteSpace(because))
        {
            // A block with no reason is a piece of work nobody can unblock,
            // because nobody knows what they are waiting for.
            throw new ArgumentException(
                "Say what is blocking it. Nobody can clear a block they cannot see.",
                nameof(because));
        }

        var from = Status;
        Status = status;

        BlockedReason = status == WorkItemStatus.Blocked ? because!.Trim() : null;

        if (status == WorkItemStatus.InProgress)
        {
            // First time only: restarting reviewed work does not rewrite when it
            // was first picked up.
            StartedAt ??= at;
            CompletedAt = null;
        }

        if (status == WorkItemStatus.Done)
        {
            CompletedAt = at;

            Raise(new WorkItemCompleted(Id, Title, AssigneeId, ProjectId, at));
        }

        if (status == WorkItemStatus.Cancelled)
        {
            CompletedAt = at;
        }

        /*
         * Deploying records nothing of its own, and that is deliberate.
         *
         * CompletedAt is when the work was accepted. Stamping the release over
         * it would lose the one date the delivery figures are worked out from,
         * and a release date kept next to it would be a column with no question
         * behind it yet — when somebody asks how long work waits between being
         * accepted and going out, the move event below already carries the
         * answer with a timestamp on it.
         */
        Raise(new WorkItemMoved(Id, from, status, because));
    }

    /// <summary>
    /// Hand it to somebody, or to nobody.
    /// </summary>
    /// <remarks>
    /// Whether that person can take it — still employed, not suspended — is not
    /// knowable from here, and is checked by the service that has the roster.
    /// </remarks>
    public void AssignTo(Guid? employeeId)
    {
        if (AssigneeId == employeeId)
        {
            return;
        }

        if (!IsOpen && employeeId is not null)
        {
            throw new InvalidOperationException(
                $"'{Title}' is {Describe(Status).ToLowerInvariant()}. Reopen it before giving it "
                + "to somebody.");
        }

        var from = AssigneeId;
        AssigneeId = employeeId;

        Raise(new WorkItemAssigned(Id, Title, from, employeeId));
    }

    public void Retitle(string title) => Title = Require(title, nameof(title));

    public void Describe(string? detail) =>
        Detail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim();

    public void Prioritise(Priority priority) => Priority = priority;

    public void MoveToProject(Guid? projectId) => ProjectId = projectId;

    public void DueBy(DateOnly? on) => DueOn = on;

    /// <summary>
    /// Estimate the work, in minutes.
    /// </summary>
    /// <remarks>
    /// Capped at a fortnight of working days. Anything larger is not an estimate,
    /// it is a project that has not been broken up, and letting it through means
    /// a board where one card hides a month of work.
    /// </remarks>
    public void Estimate(int? minutes)
    {
        if (minutes is { } value && value is < 0 or > 4800)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minutes),
                value,
                "An estimate runs from nothing to eighty hours. Anything larger is a project, "
                + "not a piece of work.");
        }

        EstimateMinutes = minutes;
    }

    /// <summary>Nothing here is a secret.</summary>
    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private static string Describe(WorkItemStatus status) => status switch
    {
        WorkItemStatus.Todo => "To do",
        WorkItemStatus.InProgress => "In progress",
        WorkItemStatus.InReview => "In review",
        _ => status.ToString(),
    };

    private static string Require(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();
}
