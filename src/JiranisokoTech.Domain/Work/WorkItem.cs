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

    private readonly List<WorkItemLabel> _labels = [];
    private readonly List<WorkItemComment> _comments = [];
    private readonly List<DoneWhen> _doneWhen = [];

    private WorkItem()
    {
        Title = string.Empty;
    }

    private WorkItem(
        int number,
        string title,
        Guid raisedById,
        Guid? projectId,
        Priority priority,
        WorkItemKind kind)
    {
        Number = number > 0
            ? number
            : throw new ArgumentOutOfRangeException(
                nameof(number), number, "Work is numbered from one.");

        Title = Require(title, nameof(title));
        RaisedById = raisedById;
        ProjectId = projectId;
        Priority = priority;
        Kind = kind;
        Status = WorkItemStatus.Todo;

        Raise(new WorkItemRaised(Id, Title, projectId, raisedById));
    }

    public static WorkItem Raise(
        int number,
        string title,
        Guid raisedById,
        Guid? projectId = null,
        Priority priority = Priority.Normal,
        WorkItemKind kind = WorkItemKind.Task) =>
        new(number, title, raisedById, projectId, priority, kind);

    /// <summary>
    /// A short number somebody can type.
    /// </summary>
    /// <remarks>
    /// Identity here is a GUIDv7 and always will be, because it can be
    /// generated before a row is saved. But nobody puts
    /// 0198f3a2-1c4d-7e8b-9a0f-2c3d4e5f6a7b in a branch name, and that is
    /// exactly what this system needs somebody to do: the only way a commit,
    /// a branch or a pull request can be tied back to the work it belongs to
    /// is if a person can reasonably write the reference while doing something
    /// else.
    ///
    /// So: sequential, short, and never reused. `feature/412-payment-api`,
    /// `Fixes #412`, or a pull request titled with it are all things a
    /// developer will actually type, and each of them is enough.
    ///
    /// Allocated by the service rather than the aggregate, for the same reason
    /// an invoice number is: knowing what the last one was is a question for
    /// the database.
    /// </remarks>
    public int Number { get; private init; }

    /// <summary>How it is written down and spoken about.</summary>
    public string Reference => $"#{Number}";

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

    /// <summary>
    /// How big it is, which decides what may sit under it.
    /// </summary>
    /// <remarks>
    /// Section 11's epics, features, stories and subtasks, as one table with a kind rather than
    /// four tables — see <see cref="WorkItemKind"/> for why, and for the rule that makes the
    /// words mean something.
    /// </remarks>
    public WorkItemKind Kind { get; private set; } = WorkItemKind.Task;

    /// <summary>
    /// The bigger piece of work this belongs to.
    /// </summary>
    /// <remarks>
    /// One parent, and the shape is a tree rather than a graph. Work that genuinely belongs to
    /// two epics is work somebody has not decided about yet, and letting it point at both means
    /// every roll-up double-counts it.
    /// </remarks>
    public Guid? ParentId { get; private set; }

    /// <summary>
    /// The sprint this is in, or null for the backlog.
    /// </summary>
    /// <remarks>
    /// The backlog is derived from this being null rather than kept as its own list. A second
    /// list would need every item to be in exactly one of the two with nothing enforcing it, and
    /// work would end up in both or in neither.
    /// </remarks>
    public Guid? SprintId { get; private set; }

    /// <summary>
    /// The words somebody put on it: "frontend", "needs-design", "kra".
    /// </summary>
    /// <remarks>
    /// A copy, not the backing list — handing EF its own navigation property makes it treat
    /// added rows as updates and then nothing saves and nothing complains.
    /// </remarks>
    public IReadOnlyList<WorkItemLabel> Labels =>
        [.. _labels.OrderBy(one => one.Text, StringComparer.Ordinal)];

    /// <summary>What has been said about it, oldest first, because it is a conversation.</summary>
    public IReadOnlyList<WorkItemComment> Comments =>
        [.. _comments.OrderBy(one => one.At).ThenBy(one => one.Id)];

    /// <summary>
    /// What has to be true before this is done.
    /// </summary>
    /// <remarks>
    /// One list doing the work of section 11's "checklists" and "acceptance criteria", because
    /// they are the same structure — a line of text and whether it is true yet — and two lists
    /// produce two half-used lists with the real criteria spread across both.
    ///
    /// The difference people describe between them is who writes them and when, and that is a
    /// habit rather than a shape. What matters is that the board cannot call something done while
    /// a line on this list is still false, which <see cref="Done"/> refuses.
    /// </remarks>
    public IReadOnlyList<DoneWhen> DoneWhen =>
        [.. _doneWhen.OrderBy(one => one.Order).ThenBy(one => one.Id)];

    public bool HasUnmetCriteria => _doneWhen.Any(one => !one.IsMet);

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

        /*
         * Done means the list of what done looks like is true. Without this the list is a
         * decoration somebody fills in and nobody reads, and the board can say a card is
         * finished while the criteria written on it are plainly not met — which is the specific
         * lie section 11's acceptance criteria exist to prevent.
         *
         * A line that has turned out not to apply is struck out with a reason rather than
         * ticked, so this refusal does not force anybody to claim something untrue in order to
         * close a card.
         */
        if (status == WorkItemStatus.Done && HasUnmetCriteria)
        {
            var left = _doneWhen.Count(one => !one.IsMet);

            throw new InvalidOperationException(
                $"{(left == 1 ? "One line of" : $"{left} lines of")} what done looks like "
                + $"{(left == 1 ? "is" : "are")} not true yet. Tick "
                + $"{(left == 1 ? "it" : "them")}, or strike out what no longer applies and say "
                + "why.");
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

    /// <summary>
    /// Say how big it is.
    /// </summary>
    /// <remarks>
    /// Kept on the aggregate although the rule about parents needs two rows. What this can check
    /// is that something with a parent does not become bigger than it, which is the mistake
    /// somebody actually makes: promoting a task to an epic while it still sits under a story.
    /// </remarks>
    public void IsA(WorkItemKind kind, WorkItemKind? parentKind)
    {
        if (ParentId is not null && parentKind is { } above && kind <= above)
        {
            throw new InvalidOperationException(
                $"A {Name(kind)} cannot sit under a {Name(above)}. Take it out of its parent "
                + "first, or make it something smaller.");
        }

        Kind = kind;
    }

    /// <summary>
    /// Put it under a bigger piece of work, or take it out.
    /// </summary>
    /// <remarks>
    /// The parent's kind is passed in because this aggregate cannot read another row, and the
    /// check has to happen somewhere that knows both: a hierarchy where an epic can sit under a
    /// subtask is one where the words have stopped meaning anything.
    ///
    /// Whether the parent is really an ancestor — the loop check — is the service's, because it
    /// has to walk.
    /// </remarks>
    public void Under(Guid? parentId, WorkItemKind? parentKind)
    {
        if (parentId == Id)
        {
            throw new InvalidOperationException("A piece of work cannot sit under itself.");
        }

        if (parentId is not null && parentKind is { } above && Kind <= above)
        {
            throw new InvalidOperationException(
                $"A {Name(Kind)} cannot sit under a {Name(above)}. A parent has to be bigger "
                + "than what is under it, or the words stop describing the shape of the work.");
        }

        ParentId = parentId;
    }

    /// <summary>Put it in a sprint, or back on the backlog.</summary>
    public void In(Guid? sprintId) => SprintId = sprintId;

    /// <summary>
    /// Put a word on it.
    /// </summary>
    /// <remarks>
    /// Idempotent after reduction, so "Frontend" on something already tagged "frontend" changes
    /// nothing rather than producing a second row that no filter will ever match twice.
    /// </remarks>
    public void Label(string text)
    {
        var reduced = Slug.From(text).Value;

        if (_labels.Any(one => one.Text == reduced))
        {
            return;
        }

        if (_labels.Count >= 12)
        {
            throw new InvalidOperationException(
                "Twelve labels is enough. Past that they are not describing the work any more, "
                + "and a board where everything is tagged is a board where nothing is.");
        }

        _labels.Add(new WorkItemLabel(reduced));
    }

    public void Unlabel(string text)
    {
        var reduced = Slug.From(text).Value;

        _labels.RemoveAll(one => one.Text == reduced);
    }

    public bool Tagged(string text) => _labels.Any(one => one.Text == Slug.From(text).Value);

    public WorkItemComment Say(Guid byEmployeeId, string body, DateTimeOffset at)
    {
        var comment = new WorkItemComment(byEmployeeId, body, at);

        _comments.Add(comment);

        return comment;
    }

    public void Reword(Guid commentId, Guid byEmployeeId, string body, DateTimeOffset at) =>
        (_comments.FirstOrDefault(one => one.Id == commentId)
            ?? throw new InvalidOperationException("That comment is not on this work."))
        .Reword(byEmployeeId, body, at);

    /// <summary>Add a line to what done looks like.</summary>
    public DoneWhen Needs(string text)
    {
        var line = new DoneWhen(text, _doneWhen.Count);

        _doneWhen.Add(line);

        return line;
    }

    public void Met(Guid lineId, Guid byEmployeeId, DateTimeOffset at) =>
        RequiredLine(lineId).Met(byEmployeeId, at);

    public void NotMet(Guid lineId) => RequiredLine(lineId).NotMet();

    public void DropLine(Guid lineId, string because) => RequiredLine(lineId).Drop(because);

    /// <summary>
    /// Take a line off the list entirely.
    /// </summary>
    /// <remarks>
    /// For the one typed twice or typed wrong, which is a different thing from one that turned
    /// out not to apply — that is struck out with a reason and stays, because the fact that it
    /// was once expected is part of what the card records.
    /// </remarks>
    public void RemoveLine(Guid lineId)
    {
        if (RequiredLine(lineId).IsTicked)
        {
            throw new InvalidOperationException(
                "That line has been ticked. Untick it first if it was wrong — removing something "
                + "somebody said was true loses the fact that they said so.");
        }

        _doneWhen.RemoveAll(one => one.Id == lineId);
    }

    /// <summary>Nothing here is a secret.</summary>
    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    /// <summary>
    /// The kind, as somebody would say it in a sentence.
    /// </summary>
    /// <remarks>
    /// Every arm written out, and an unknown value throws rather than falling into the last one.
    /// The first version ended with <c>_ => "subtask"</c>, and that default arm is how ten
    /// existing cards came to be labelled subtasks the hour this shipped: the migration gave the
    /// new column EF's default of zero, which is not a value this enum has, and the switch
    /// quietly answered anyway.
    ///
    /// The same reasoning the document permissions switch already uses, arrived at the same way —
    /// by a screen being wrong. A switch that cannot answer should say so, because the alternative
    /// is a plausible answer nobody checks.
    /// </remarks>
    public static string Name(WorkItemKind kind) => kind switch
    {
        WorkItemKind.Epic => "epic",
        WorkItemKind.Feature => "feature",
        WorkItemKind.Story => "story",
        WorkItemKind.Task => "task",
        WorkItemKind.Subtask => "subtask",
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind), kind, "Nothing has decided what this kind of work is called."),
    };

    private DoneWhen RequiredLine(Guid lineId) =>
        _doneWhen.FirstOrDefault(one => one.Id == lineId)
        ?? throw new InvalidOperationException("That line is not on this work.");

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
