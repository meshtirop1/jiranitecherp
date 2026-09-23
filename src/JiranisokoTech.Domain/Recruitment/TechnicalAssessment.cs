using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Recruitment;

/// <summary>
/// What sort of exercise somebody was set.
/// </summary>
/// <remarks>
/// Three, and the distinction that matters is who holds the clock. A take-home is work done
/// unattended against a deadline; a live exercise is somebody watching; a written test is
/// neither. They are kept apart because the fair thing to do when a deadline is missed
/// differs in each case, and a single kind would make that somebody's judgement with nothing
/// recorded to base it on.
/// </remarks>
public enum AssessmentKind
{
    TakeHome = 1,
    LiveExercise = 2,
    WrittenTest = 3,
}

/// <summary>
/// Where an exercise has got to.
/// </summary>
/// <remarks>
/// There is deliberately no Expired. A deadline that has passed is a fact about the clock,
/// not a state somebody has to move the row into — and a status that only becomes true when a
/// job runs is a status that is wrong between midnight and whenever that job ran. Whether it
/// is late is computed from the date, the same argument that keeps invoices from having an
/// Overdue status.
/// </remarks>
public enum AssessmentStatus
{
    Assigned = 1,
    Submitted = 2,
    Marked = 3,
    Cancelled = 4,
}

/// <summary>
/// An exercise set between the interview and the offer.
/// </summary>
/// <remarks>
/// Section 7's chain ran requisition, approval, opening, published, application, screening,
/// interview, offer letter — and the step between the last two was missing. Almost every firm
/// that hires engineers sets something, and without a record of it the decision to make an
/// offer rests on a conversation nobody wrote down.
///
/// <b>Its own table rather than a fourth InterviewKind.</b> An interview is a conversation at
/// a time with people in the room; an exercise is work done unattended against a deadline.
/// One table for both would mean a panel and a scheduled time on rows that have neither, and
/// every read of either would carry the other's nulls.
///
/// <b>Flat, with no rubric.</b> A marking scheme with weighted criteria is the obvious next
/// thing and it is a trap at this size: it turns a judgement into arithmetic, and the
/// arithmetic then has to be defended to a candidate who asks. What is recorded is the same
/// four-point recommendation an interviewer gives, plus what the marker actually wrote.
/// </remarks>
public sealed class TechnicalAssessment : Entity, IAuditable
{
    private TechnicalAssessment()
    {
        Title = string.Empty;
        Instructions = string.Empty;
    }

    private TechnicalAssessment(
        Guid applicationId,
        AssessmentKind kind,
        string title,
        string instructions,
        DateOnly dueBy,
        Guid? setBy,
        DateTimeOffset at)
    {
        if (applicationId == Guid.Empty)
        {
            throw new ArgumentException(
                "An exercise has to be set for somebody's application.", nameof(applicationId));
        }

        ApplicationId = applicationId;
        Kind = kind;
        Title = Required(title, nameof(title));
        Instructions = Required(instructions, nameof(instructions));
        DueBy = dueBy;
        SetBy = setBy;
        Status = AssessmentStatus.Assigned;
        SetAt = at;

        Raise(new AssessmentSet(Id, applicationId, Title, dueBy, at));
    }

    public static TechnicalAssessment Set(
        Guid applicationId,
        AssessmentKind kind,
        string title,
        string instructions,
        DateOnly dueBy,
        Guid? setBy = null,
        DateTimeOffset at = default) =>
        new(applicationId, kind, title, instructions, dueBy, setBy, at);

    public Guid ApplicationId { get; private init; }

    public AssessmentKind Kind { get; private init; }

    /// <summary>What the exercise is called.</summary>
    public string Title { get; private set; }

    /// <summary>
    /// What the candidate was asked to do, in full.
    /// </summary>
    /// <remarks>
    /// Stored rather than linked, and that is the point of having it here at all. A candidate
    /// who asks six months later what they were set deserves an answer, and a link to a
    /// document somebody has since edited is not one.
    /// </remarks>
    public string Instructions { get; private set; }

    public DateOnly DueBy { get; private set; }

    /// <summary>Who set it.</summary>
    public Guid? SetBy { get; private init; }

    public AssessmentStatus Status { get; private set; }

    public DateTimeOffset SetAt { get; private init; }

    /// <summary>Where the work is, once it has been handed in.</summary>
    public string? SubmissionUrl { get; private set; }

    public DateTimeOffset? SubmittedAt { get; private set; }

    /// <summary>The same four-point recommendation an interviewer gives.</summary>
    public Recommendation? Result { get; private set; }

    /// <summary>What the marker wrote.</summary>
    public string? MarkerNotes { get; private set; }

    public Guid? MarkedBy { get; private init; }

    public DateTimeOffset? MarkedAt { get; private set; }

    /// <summary>Why it was called off, which is the only thing a cancelled one is good for.</summary>
    public string? CancelledBecause { get; private set; }

    /// <summary>
    /// Still waiting on somebody.
    /// </summary>
    /// <remarks>
    /// The figure the offer gate reads. Both Assigned and Submitted count: the first is out
    /// with the candidate, the second is back and unmarked, and an offer made in either state
    /// is an offer made on evidence nobody has looked at.
    /// </remarks>
    public bool IsOutstanding =>
        Status is AssessmentStatus.Assigned or AssessmentStatus.Submitted;

    /// <summary>
    /// Whether the deadline has gone by with nothing handed in.
    /// </summary>
    /// <remarks>
    /// Computed, never stored, and there is no Expired status for the same reason there is no
    /// Overdue on an invoice: being late is what is true of it at the moment somebody looks.
    /// A stored flag would need a nightly job to stay true and would be wrong between
    /// midnight and whenever that job ran.
    /// </remarks>
    public bool IsLateOn(DateOnly today) =>
        Status == AssessmentStatus.Assigned && DueBy < today;

    /// <summary>
    /// The candidate has handed something in.
    /// </summary>
    /// <remarks>
    /// Accepted after the deadline rather than refused. Somebody who worked late and handed in
    /// on Monday morning has done the exercise, and a system that refused the submission would
    /// throw away the only evidence there is — leaving whoever decides with a blank row and a
    /// harder conversation. That it was late is visible from the two dates.
    /// </remarks>
    public void HandedIn(string? submissionUrl, DateTimeOffset at)
    {
        if (Status != AssessmentStatus.Assigned)
        {
            /*
             * Silently, rather than throwing. A candidate pressing submit twice, or a marker
             * recording a second link, is an ordinary event and not an error worth showing
             * somebody a stack trace over — and the first submission is the one that counts.
             */
            return;
        }

        Status = AssessmentStatus.Submitted;
        SubmissionUrl = Trimmed(submissionUrl);
        SubmittedAt = at;

        Raise(new AssessmentHandedIn(Id, ApplicationId, at));
    }

    /// <summary>
    /// Somebody has marked it.
    /// </summary>
    /// <remarks>
    /// Notes are required, and that is the rule worth having. A recommendation on its own is a
    /// verdict nobody can check, and the conversation this record exists for — why did we not
    /// take that person forward — needs the sentence rather than the score.
    ///
    /// Marking something never handed in is refused, because there is nothing to have marked.
    /// An exercise that was not submitted is cancelled with a reason instead.
    /// </remarks>
    public void Marked(Recommendation result, string notes, DateTimeOffset at)
    {
        if (Status == AssessmentStatus.Marked)
        {
            return;
        }

        if (Status != AssessmentStatus.Submitted)
        {
            throw new InvalidOperationException(
                "Nothing has been handed in yet, so there is nothing to mark. If the deadline "
                + "has gone by, call the exercise off and say why.");
        }

        if (string.IsNullOrWhiteSpace(notes))
        {
            throw new ArgumentException(
                "Say what you made of it. A recommendation with no reasoning behind it is a "
                + "verdict nobody can check, and this is the record somebody reads when a "
                + "candidate asks why.",
                nameof(notes));
        }

        Status = AssessmentStatus.Marked;
        Result = result;
        MarkerNotes = notes.Trim();
        MarkedAt = at;

        Raise(new AssessmentMarked(Id, ApplicationId, result, at));
    }

    /// <summary>
    /// Call it off.
    /// </summary>
    /// <remarks>
    /// The route out for an exercise that was never handed in, and the reason is required for
    /// the same reason a lost opportunity's is: without it the row says somebody did not do
    /// something, which nobody can act on and nobody reads twice.
    /// </remarks>
    public void Cancel(string because, DateTimeOffset at)
    {
        if (Status is AssessmentStatus.Marked or AssessmentStatus.Cancelled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(because))
        {
            throw new ArgumentException(
                "Say why it was called off. Nobody can act on a row that only says an "
                + "exercise did not happen.",
                nameof(because));
        }

        Status = AssessmentStatus.Cancelled;
        CancelledBecause = because.Trim();

        Raise(new AssessmentCancelled(Id, ApplicationId, CancelledBecause, at));
    }

    /// <summary>Give them longer.</summary>
    /// <remarks>
    /// Only forwards, and only while it is still out. Moving a deadline closer after the fact
    /// would make somebody late retrospectively, and extending one already handed in changes
    /// a date that a decision has been recorded against.
    /// </remarks>
    public void DueLater(DateOnly dueBy)
    {
        if (Status != AssessmentStatus.Assigned || dueBy <= DueBy)
        {
            return;
        }

        DueBy = dueBy;
    }

    /// <summary>
    /// The instructions and the marker's notes stay out of the trail.
    /// </summary>
    /// <remarks>
    /// The instructions because they are the same paragraphs on every row and would bury every
    /// other change in the trail. The notes because they are a judgement about a person who
    /// does not work here, written to be read by the two or three people deciding — and the
    /// trail is read by everybody holding audit.view.
    /// </remarks>
    public static IReadOnlySet<string> AuditExcludes { get; } =
        new HashSet<string> { nameof(Instructions), nameof(MarkerNotes) };

    private static string Required(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record AssessmentSet(
    Guid AssessmentId,
    Guid ApplicationId,
    string Title,
    DateOnly DueBy,
    DateTimeOffset At) : DomainEvent;

public sealed record AssessmentHandedIn(
    Guid AssessmentId, Guid ApplicationId, DateTimeOffset At) : DomainEvent;

public sealed record AssessmentMarked(
    Guid AssessmentId,
    Guid ApplicationId,
    Recommendation Result,
    DateTimeOffset At) : DomainEvent;

public sealed record AssessmentCancelled(
    Guid AssessmentId, Guid ApplicationId, string Because, DateTimeOffset At) : DomainEvent;
