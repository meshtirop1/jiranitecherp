using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Work;

/// <summary>Where a sprint is in its life.</summary>
public enum SprintState
{
    /// <summary>Being filled. Work can be added and taken out freely.</summary>
    Planned = 1,

    Running = 2,

    Finished = 3,
}

/// <summary>
/// A fixed period the firm has agreed to work on a fixed set of things.
/// </summary>
/// <remarks>
/// Section 11.
///
/// <b>A sprint is a period, and the backlog is everything not in one.</b> That is the whole
/// model, and it is why there is no backlog table: a second table would need every item to be in
/// exactly one of the two and nothing would enforce it, so work would end up in both or neither.
/// Deriving the backlog from a null means it cannot disagree with itself.
///
/// <b>One sprint runs at a time.</b> Not a preference — "the current sprint" is a phrase this
/// system has to be able to answer, and with two running it has no answer. The rule lives in the
/// service, which is the only thing that can see the other sprints.
///
/// <b>Finishing one says what did not get done.</b> Unfinished work goes back to the backlog and
/// the screen says how many, rather than the sprint quietly closing over it. A sprint that ends
/// by hiding what it missed is a sprint nobody learns anything from, and the number is the one
/// piece of information the ceremony exists to produce.
/// </remarks>
public sealed class Sprint : Entity, IAuditable
{
    private Sprint() => Name = string.Empty;

    private Sprint(string name, DateOnly starts, DateOnly ends, string? goal)
    {
        if (ends < starts)
        {
            throw new ArgumentException("A sprint cannot end before it starts.", nameof(ends));
        }

        Name = Require(name, nameof(name), 120);
        Starts = starts;
        Ends = ends;
        Goal = string.IsNullOrWhiteSpace(goal) ? null : goal.Trim();
        State = SprintState.Planned;
    }

    public static Sprint Plan(string name, DateOnly starts, DateOnly ends, string? goal = null) =>
        new(name, starts, ends, goal);

    public string Name { get; private set; }

    /// <summary>
    /// What the sprint is for, in a sentence.
    /// </summary>
    /// <remarks>
    /// Optional, and asked for. A sprint with a goal can be judged; a sprint that is only a list
    /// of cards can only be counted, and counting cards is how a team ends up finishing eleven
    /// small things and none of the one that mattered.
    /// </remarks>
    public string? Goal { get; private set; }

    public DateOnly Starts { get; private set; }

    public DateOnly Ends { get; private set; }

    public SprintState State { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>What was still unfinished when it ended, recorded rather than inferred.</summary>
    /// <remarks>
    /// Kept on the row because it is a fact about a moment. Counting open items later gives a
    /// different answer every time somebody finishes one afterwards, which would quietly improve
    /// the history of every sprint the firm has ever run.
    /// </remarks>
    public int? CarriedOver { get; private set; }

    public bool IsRunning => State == SprintState.Running;

    public bool IsOver => State == SprintState.Finished;

    /// <summary>Takes work: being planned, or running.</summary>
    public bool Accepts => State != SprintState.Finished;

    public int Days => Ends.DayNumber - Starts.DayNumber + 1;

    public void Describe(string name, string? goal, DateOnly starts, DateOnly ends)
    {
        if (IsOver)
        {
            throw new InvalidOperationException(
                $"{Name} is over. Its dates are what the firm actually worked to, and changing "
                + "them now would rewrite the period a finished sprint reports on.");
        }

        if (ends < starts)
        {
            throw new ArgumentException("A sprint cannot end before it starts.", nameof(ends));
        }

        Name = Require(name, nameof(name), 120);
        Goal = string.IsNullOrWhiteSpace(goal) ? null : goal.Trim();
        Starts = starts;
        Ends = ends;
    }

    public void Start(DateTimeOffset at)
    {
        if (State != SprintState.Planned)
        {
            throw new InvalidOperationException(
                IsRunning ? $"{Name} is already running." : $"{Name} is over.");
        }

        State = SprintState.Running;
        StartedAt = at;

        Raise(new SprintStarted(Id, Name, Starts, Ends, at));
    }

    /// <summary>
    /// End it, recording how much did not get done.
    /// </summary>
    /// <remarks>
    /// The count comes from the caller because the sprint does not hold its items — they point at
    /// it, which is what lets a piece of work move between sprints without either sprint being
    /// rewritten.
    /// </remarks>
    public void Finish(int carriedOver, DateTimeOffset at)
    {
        if (State != SprintState.Running)
        {
            throw new InvalidOperationException(
                State == SprintState.Planned
                    ? $"{Name} has not started. Delete it or start it first."
                    : $"{Name} is already over.");
        }

        State = SprintState.Finished;
        FinishedAt = at;
        CarriedOver = carriedOver;

        Raise(new SprintFinished(Id, Name, carriedOver, at));
    }

    /// <summary>Nothing here is a secret.</summary>
    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private static string Require(string value, string parameter, int longest)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("This cannot be blank.", parameter);
        }

        var trimmed = value.Trim();

        return trimmed.Length > longest
            ? throw new ArgumentException(
                $"This cannot be longer than {longest} characters.", parameter)
            : trimmed;
    }
}

public sealed record SprintStarted(
    Guid SprintId, string Name, DateOnly Starts, DateOnly Ends, DateTimeOffset At) : DomainEvent;

/// <summary>
/// A sprint ended.
/// </summary>
/// <remarks>
/// Carries what was carried over, because that number is the only thing anybody wants to know
/// about a sprint that has just ended and it would otherwise have to be recomputed by a handler
/// that cannot get the same answer twice.
/// </remarks>
public sealed record SprintFinished(
    Guid SprintId, string Name, int CarriedOver, DateTimeOffset At) : DomainEvent;
