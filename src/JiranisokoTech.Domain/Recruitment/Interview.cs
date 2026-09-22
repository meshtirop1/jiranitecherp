using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Recruitment;

public enum InterviewKind
{
    /// <summary>A short call to check the basics.</summary>
    Screening = 1,

    Technical = 2,

    /// <summary>With whoever the person would answer to.</summary>
    Final = 3,
}

public enum InterviewStatus
{
    Scheduled = 1,
    Held = 2,
    Cancelled = 3,

    /// <summary>Nobody came. Recorded rather than deleted.</summary>
    NoShow = 4,
}

/// <summary>
/// How somebody did, in the words of one interviewer.
/// </summary>
/// <remarks>
/// Four points and no middle. A five-point scale collects threes, and a three
/// is a person saying "somebody else decide" — which is the one thing a
/// scorecard exists to stop.
/// </remarks>
public enum Recommendation
{
    StrongNo = 1,
    No = 2,
    Yes = 3,
    StrongYes = 4,
}

/// <summary>
/// A conversation with a candidate, and what the people in it thought.
/// </summary>
/// <remarks>
/// The scorecards are owned by the interview rather than standing alone,
/// because every rule about them is about the set: who has submitted, who has
/// not, whether the same person has scored twice. A scorecard reachable without
/// its interview is one somebody can file against a conversation that never
/// happened.
/// </remarks>
public sealed class Interview : Entity, IAuditable
{
    private readonly List<Scorecard> _scorecards = [];
    private readonly List<InterviewPanellist> _panel = [];

    private Interview()
    {
    }

    private Interview(
        Guid applicationId,
        InterviewKind kind,
        DateTimeOffset at,
        IEnumerable<Guid> panel,
        string? where)
    {
        ApplicationId = applicationId;
        Kind = kind;
        ScheduledFor = at;
        Where = string.IsNullOrWhiteSpace(where) ? null : where.Trim();
        Status = InterviewStatus.Scheduled;

        _panel.AddRange(panel.Distinct().Select(person => InterviewPanellist.Of(person)));

        if (_panel.Count == 0)
        {
            // An interview with nobody in it is a diary entry. The panel is
            // also who the scorecards are expected from.
            throw new ArgumentException(
                "An interview needs at least one interviewer.", nameof(panel));
        }

        Raise(new InterviewScheduled(Id, applicationId, kind, at, _panel.Count));
    }

    public static Interview Schedule(
        Guid applicationId,
        InterviewKind kind,
        DateTimeOffset at,
        IEnumerable<Guid> panel,
        string? where = null) =>
        new(applicationId, kind, at, panel, where);

    public Guid ApplicationId { get; private init; }

    public InterviewKind Kind { get; private init; }

    public DateTimeOffset ScheduledFor { get; private set; }

    /// <summary>A room, or a meeting link.</summary>
    public string? Where { get; private set; }

    public InterviewStatus Status { get; private set; }

    public string? Outcome { get; private set; }

    /// <remarks>
    /// A copy. The panel is who scorecards are expected from, and it is settled
    /// when the interview is scheduled; a caller holding the real list could add
    /// somebody to the room after the fact.
    /// </remarks>
    public IReadOnlyList<InterviewPanellist> Panel => _panel.ToList();

    /// <inheritdoc cref="Panel"/>
    public IReadOnlyList<Scorecard> Scorecards => _scorecards.ToList();

    /// <summary>Everybody on the panel has said what they thought.</summary>
    public bool IsScored => _panel.Count > 0 && _scorecards.Count >= _panel.Count;

    /// <summary>Panellists who have not submitted yet.</summary>
    public IReadOnlyList<Guid> Outstanding => _panel
        .Select(one => one.EmployeeId)
        .Where(person => _scorecards.All(card => card.InterviewerId != person))
        .ToList();

    public void Rearrange(DateTimeOffset at, string? where)
    {
        if (Status != InterviewStatus.Scheduled)
        {
            throw new InvalidOperationException(
                $"This interview is {Status.ToString().ToLowerInvariant()} and cannot be moved.");
        }

        ScheduledFor = at;
        Where = string.IsNullOrWhiteSpace(where) ? Where : where.Trim();

        Raise(new InterviewRearranged(Id, ApplicationId, at));
    }

    public void Held(DateTimeOffset at)
    {
        if (Status != InterviewStatus.Scheduled)
        {
            throw new InvalidOperationException(
                $"This interview is already {Status.ToString().ToLowerInvariant()}.");
        }

        Status = InterviewStatus.Held;

        Raise(new InterviewHeld(Id, ApplicationId, at, [.. Outstanding]));
    }

    public void Cancel(string reason, DateTimeOffset at)
    {
        if (Status is InterviewStatus.Cancelled or InterviewStatus.Held)
        {
            throw new InvalidOperationException(
                $"This interview is already {Status.ToString().ToLowerInvariant()}.");
        }

        Status = InterviewStatus.Cancelled;
        Outcome = Require(reason, nameof(reason));

        Raise(new InterviewCancelled(Id, ApplicationId, Outcome, at));
    }

    public void NobodyCame(DateTimeOffset at)
    {
        if (Status != InterviewStatus.Scheduled)
        {
            throw new InvalidOperationException(
                $"This interview is already {Status.ToString().ToLowerInvariant()}.");
        }

        Status = InterviewStatus.NoShow;
        Outcome = "Nobody came.";

        Raise(new InterviewCancelled(Id, ApplicationId, Outcome, at));
    }

    /// <summary>
    /// Record what one interviewer thought.
    /// </summary>
    /// <remarks>
    /// Only after the interview has happened, and only from somebody who was on
    /// the panel. A scorecard filed before the conversation is an opinion
    /// formed from a CV, and one from somebody who was not there is hearsay —
    /// both are worse than no scorecard at all, because they look the same on
    /// the page as a real one.
    /// </remarks>
    public void Score(
        Guid interviewerId,
        Recommendation recommendation,
        string notes,
        DateTimeOffset at)
    {
        if (Status != InterviewStatus.Held)
        {
            throw new InvalidOperationException(
                Status == InterviewStatus.Scheduled
                    ? "This interview has not been marked as held yet."
                    : $"This interview was {Status.ToString().ToLowerInvariant()}.");
        }

        if (_panel.All(one => one.EmployeeId != interviewerId))
        {
            throw new InvalidOperationException(
                "Only somebody who was on the panel can score this interview.");
        }

        if (_scorecards.Any(card => card.InterviewerId == interviewerId))
        {
            throw new InvalidOperationException(
                "You have already scored this interview. A second opinion from the same person "
                + "is a changed mind, and that belongs in a conversation rather than a second "
                + "row.");
        }

        if (string.IsNullOrWhiteSpace(notes))
        {
            // A recommendation with no reasoning is a vote. Somebody reading
            // this in six months needs to know what was actually said.
            throw new ArgumentException(
                "Say what you saw. A recommendation on its own tells nobody anything.",
                nameof(notes));
        }

        _scorecards.Add(Scorecard.Of(interviewerId, recommendation, notes, at));

        Raise(new ScorecardSubmitted(Id, ApplicationId, interviewerId, recommendation, at));

        if (IsScored)
        {
            Raise(new InterviewFullyScored(Id, ApplicationId, Verdict(), at));
        }
    }

    /// <summary>
    /// What the panel thought, taken together.
    /// </summary>
    /// <remarks>
    /// A single strong no carries the day. Panels exist so that one person who
    /// saw something serious can stop a hire, and averaging that away is how a
    /// firm ends up hiring somebody three of four people had doubts about.
    /// </remarks>
    public Recommendation Verdict()
    {
        if (_scorecards.Count == 0)
        {
            throw new InvalidOperationException("Nobody has scored this interview.");
        }

        if (_scorecards.Any(card => card.Recommendation == Recommendation.StrongNo))
        {
            return Recommendation.StrongNo;
        }

        var average = _scorecards.Average(card => (int)card.Recommendation);

        return (Recommendation)(int)Math.Round(average, MidpointRounding.ToZero);
    }

    /// <summary>
    /// Interview notes are about a person outside the firm and are read inside
    /// it, but they are the record of a hiring decision and belong in the trail.
    /// </summary>
    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private static string Require(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();
}

/// <summary>Somebody expected in the room.</summary>
public sealed class InterviewPanellist
{
    private InterviewPanellist()
    {
    }

    public static InterviewPanellist Of(Guid employeeId) =>
        new() { Id = Guid.CreateVersion7(), EmployeeId = employeeId };

    public Guid Id { get; private init; }

    public Guid EmployeeId { get; private init; }
}

/// <summary>One interviewer's view, written down once.</summary>
public sealed class Scorecard
{
    private Scorecard()
    {
        Notes = string.Empty;
    }

    internal static Scorecard Of(
        Guid interviewerId, Recommendation recommendation, string notes, DateTimeOffset at) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            InterviewerId = interviewerId,
            Recommendation = recommendation,
            Notes = notes.Trim(),
            SubmittedAt = at,
        };

    public Guid Id { get; private init; }

    public Guid InterviewerId { get; private init; }

    public Recommendation Recommendation { get; private init; }

    public string Notes { get; private init; } = string.Empty;

    public DateTimeOffset SubmittedAt { get; private init; }
}

public sealed record InterviewScheduled(
    Guid InterviewId,
    Guid ApplicationId,
    InterviewKind Kind,
    DateTimeOffset At,
    int PanelSize) : DomainEvent;

public sealed record InterviewRearranged(
    Guid InterviewId, Guid ApplicationId, DateTimeOffset At) : DomainEvent;

/// <summary>
/// It happened, and these people still owe a scorecard.
/// </summary>
/// <remarks>
/// The outstanding list travels on the event so that whatever chases people for
/// feedback does not have to reload the interview to find out who to chase.
/// </remarks>
public sealed record InterviewHeld(
    Guid InterviewId,
    Guid ApplicationId,
    DateTimeOffset At,
    IReadOnlyList<Guid> Awaiting) : DomainEvent;

public sealed record InterviewCancelled(
    Guid InterviewId, Guid ApplicationId, string Reason, DateTimeOffset At) : DomainEvent;

public sealed record ScorecardSubmitted(
    Guid InterviewId,
    Guid ApplicationId,
    Guid InterviewerId,
    Recommendation Recommendation,
    DateTimeOffset At) : DomainEvent;

public sealed record InterviewFullyScored(
    Guid InterviewId,
    Guid ApplicationId,
    Recommendation Verdict,
    DateTimeOffset At) : DomainEvent;
