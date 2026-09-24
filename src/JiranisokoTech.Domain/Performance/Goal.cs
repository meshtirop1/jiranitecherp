using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Performance;

/// <summary>How a goal ended.</summary>
/// <remarks>
/// Four outcomes rather than done and not done, because the two that matter most are in the
/// middle. A goal half met is the ordinary result of an honest goal, and one dropped because the
/// firm changed direction is not a failure by the person who held it — recording both as "missed"
/// would make the record useless for exactly the conversation it exists to support.
/// </remarks>
public enum GoalOutcome
{
    Achieved = 1,
    Partly = 2,
    Missed = 3,

    /// <summary>No longer being pursued, for a reason that is not about the person.</summary>
    Dropped = 4,
}

/// <summary>
/// Something said about a goal while it was live.
/// </summary>
/// <remarks>
/// The reason a goal is more than two dates and a sentence. A goal with no conversation on it is
/// one nobody remembers by review time, and the review then becomes an argument about what was
/// meant in January. Anybody who may see the goal may add to this — the person holding it and
/// whoever set it — because a note only one of them can write is a monologue.
/// </remarks>
public sealed class GoalNote : Entity
{
    private GoalNote() => Note = string.Empty;

    internal GoalNote(Guid byEmployeeId, string note, DateTimeOffset at)
    {
        ByEmployeeId = byEmployeeId;
        Note = note;
        At = at;
    }

    public Guid ByEmployeeId { get; private init; }

    public string Note { get; private init; }

    public DateTimeOffset At { get; private init; }
}

/// <summary>
/// Something one person is trying to achieve, by a date.
/// </summary>
/// <remarks>
/// Section 6.
///
/// <b>A measure is required.</b> The single rule here that does any real work. "Improve
/// communication" is not a goal; it is a mood, and at review time it can be argued either way by
/// whoever is more confident. Making the measure a required field means the argument happens when
/// the goal is set — which is when it is cheap, and when the person holding it is in the room.
///
/// <b>Nothing here is scored.</b> A goal has an outcome and a review has a rating, and the rating
/// is never computed from the outcomes. It is tempting — four goals, three achieved, that is a
/// number — and it is the thing that turns a goal-setting conversation into a negotiation about
/// how easy this year's goals should be. See the note on the rating itself.
///
/// <b>It is closed, not deleted.</b> A goal quietly removed leaves a person's record saying they
/// were working on three things when they were working on five, and the two that disappeared are
/// always the awkward ones.
/// </remarks>
public sealed class Goal : Entity, IAuditable
{
    private readonly List<GoalNote> _notes = [];

    private Goal()
    {
        Title = string.Empty;
        Measure = string.Empty;
    }

    private Goal(
        Guid forEmployeeId,
        Guid setByEmployeeId,
        string title,
        string measure,
        DateOnly from,
        DateOnly to,
        Guid? cycleId,
        DateTimeOffset at)
    {
        if (to < from)
        {
            throw new ArgumentException(
                "A goal cannot end before it starts.", nameof(to));
        }

        ForEmployeeId = forEmployeeId;
        SetByEmployeeId = setByEmployeeId;
        Title = Require(title, nameof(title), 300);
        Measure = Require(measure, nameof(measure), 1_000);
        From = from;
        To = to;
        CycleId = cycleId;
        SetAt = at;

        Raise(new GoalSet(Id, forEmployeeId, setByEmployeeId, Title, To, at));
    }

    public static Goal Set(
        Guid forEmployeeId,
        Guid setByEmployeeId,
        string title,
        string measure,
        DateOnly from,
        DateOnly to,
        DateTimeOffset at,
        Guid? cycleId = null) =>
        new(forEmployeeId, setByEmployeeId, title, measure, from, to, cycleId, at);

    /// <summary>Whose goal it is.</summary>
    public Guid ForEmployeeId { get; private init; }

    /// <summary>
    /// Who set it with them.
    /// </summary>
    /// <remarks>
    /// Recorded because a goal somebody set for themselves and a goal their manager set are
    /// different things at review time, and six months later nobody remembers which this was.
    /// </remarks>
    public Guid SetByEmployeeId { get; private init; }

    public string Title { get; private set; }

    /// <summary>
    /// How anybody will know whether it happened.
    /// </summary>
    /// <remarks>
    /// Required, and the whole point. It does not have to be a number — "the on-call rota is
    /// written down and two other people have run a deployment" is a perfectly good measure —
    /// but it has to be something two people can look at and agree about.
    /// </remarks>
    public string Measure { get; private set; }

    public string? Detail { get; private set; }

    public DateOnly From { get; private set; }

    public DateOnly To { get; private set; }

    /// <summary>
    /// The review cycle this belongs to, if any.
    /// </summary>
    /// <remarks>
    /// Optional, because a goal set in the middle of a year over something that came up is a real
    /// goal and refusing it until the next cycle opens would be a system telling somebody to wait
    /// six months before starting.
    /// </remarks>
    public Guid? CycleId { get; private set; }

    public DateTimeOffset SetAt { get; private init; }

    public GoalOutcome? Outcome { get; private set; }

    public string? Verdict { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public bool IsOpen => Outcome is null;

    public IReadOnlyList<GoalNote> Notes =>
        [.. _notes.OrderByDescending(one => one.At).ThenByDescending(one => one.Id)];

    /// <summary>Past its date and still open, which is the only list worth a nudge.</summary>
    public bool IsOverdueOn(DateOnly today) => IsOpen && today > To;

    public void Describe(string title, string measure, string? detail)
    {
        Refuse();

        Title = Require(title, nameof(title), 300);
        Measure = Require(measure, nameof(measure), 1_000);
        Detail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim();
    }

    public void Runs(DateOnly from, DateOnly to)
    {
        Refuse();

        if (to < from)
        {
            throw new ArgumentException("A goal cannot end before it starts.", nameof(to));
        }

        From = from;
        To = to;
    }

    public void Note(Guid byEmployeeId, string note, DateTimeOffset at)
    {
        Refuse();

        _notes.Add(new GoalNote(byEmployeeId, Require(note, nameof(note), 4_000), at));
    }

    /// <summary>
    /// Say how it went.
    /// </summary>
    /// <remarks>
    /// A sentence is required alongside the outcome. "Partly" on its own is the least useful
    /// thing anybody can write down: the reader wants to know which part, and the person who
    /// knew is whoever pressed the button.
    /// </remarks>
    public void Close(GoalOutcome outcome, string verdict, DateTimeOffset at)
    {
        Refuse();

        Outcome = outcome;
        Verdict = Require(verdict, nameof(verdict), 4_000);
        ClosedAt = at;

        Raise(new GoalClosed(Id, ForEmployeeId, Title, outcome, at));
    }

    /// <summary>
    /// Open it again, because somebody closed the wrong one.
    /// </summary>
    /// <remarks>
    /// Allowed, unlike an audit entry. A goal is a working agreement between two people rather
    /// than a record of what was known at a moment, and a mis-press that could not be undone
    /// would leave the only fix being a second goal with the same words — which reads as though
    /// somebody was set the same thing twice.
    /// </remarks>
    public void Reopen()
    {
        Outcome = null;
        Verdict = null;
        ClosedAt = null;
    }

    /// <summary>Nothing here is a secret from the two people it concerns.</summary>
    /// <remarks>
    /// The audit trail records that a goal changed, and it records the words. That is deliberate:
    /// unlike a salary, the words of a goal are the thing both people are relying on, and a trail
    /// that redacted them could not settle a disagreement about what was agreed. Who may read the
    /// trail is a separate question, answered by audit.view.
    /// </remarks>
    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    /// <summary>Nothing is changed on a closed goal without reopening it first.</summary>
    private void Refuse()
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException(
                "This goal has been closed. Reopen it if that was a mistake, or set a new one — "
                + "editing what a closed goal said would change what somebody was judged on "
                + "after the judging.");
        }
    }

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

public sealed record GoalSet(
    Guid GoalId,
    Guid ForEmployeeId,
    Guid SetByEmployeeId,
    string Title,
    DateOnly By,
    DateTimeOffset At) : DomainEvent;

public sealed record GoalClosed(
    Guid GoalId,
    Guid ForEmployeeId,
    string Title,
    GoalOutcome Outcome,
    DateTimeOffset At) : DomainEvent;
