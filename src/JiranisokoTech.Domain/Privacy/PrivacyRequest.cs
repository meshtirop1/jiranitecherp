using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Privacy;

/// <summary>What a data subject is asking for.</summary>
/// <remarks>
/// The two rights this firm can actually answer. Rectification is left out because it is already
/// served by a screen — somebody corrects their own contact details on their profile and HR
/// corrects the rest — and a workflow for it would be a slower way to do something that already
/// takes one click.
/// </remarks>
public enum PrivacyAsk
{
    /// <summary>What do you hold about me.</summary>
    Access = 1,

    /// <summary>Delete what you hold about me.</summary>
    Erasure = 2,
}

/// <summary>Who the request is about.</summary>
/// <remarks>
/// A kind and an identifier rather than a foreign key, because the subject may be a candidate, a
/// member of staff, or somebody the firm holds data about who is neither — and a nullable column
/// per kind would grow every time the answer to "who else do we hold data about" changed.
/// </remarks>
public enum SubjectKind
{
    Employee = 1,
    Candidate = 2,
    Other = 3,
}

/// <summary>One class of the subject's data.</summary>
/// <remarks>
/// An enum rather than free text, because a class somebody can type is a class nothing can sweep
/// and nothing can report on. Each is a thing a person would recognise being asked about.
/// </remarks>
public enum DataClass
{
    /// <summary>Name, job title, dates, department.</summary>
    StaffRecord = 1,

    /// <summary>Phone, personal email, address, date of birth.</summary>
    ContactDetails = 2,

    /// <summary>National identity and tax numbers.</summary>
    IdentityNumbers = 3,

    /// <summary>Next of kin.</summary>
    NextOfKin = 4,

    /// <summary>Salary and employment terms.</summary>
    PayAndTerms = 5,

    /// <summary>Appraisals, goals, review notes.</summary>
    Performance = 6,

    /// <summary>Application, CV, interview scorecards.</summary>
    Recruitment = 7,

    /// <summary>Uploaded files and photographs.</summary>
    Documents = 8,

    /// <summary>Who changed what, and when.</summary>
    AuditTrail = 9,
}

/// <summary>What was done about one class of data.</summary>
public enum OutcomeKind
{
    /// <summary>The values are gone.</summary>
    Erased = 1,

    /// <summary>The row stays and the person cannot be identified from it.</summary>
    Anonymised = 2,

    /// <summary>Kept, on a stated basis, until a stated date.</summary>
    Retained = 3,

    /// <summary>Not done, for a stated reason.</summary>
    Refused = 4,

    /// <summary>Handed to the subject. Only meaningful on an access request.</summary>
    Disclosed = 5,
}

/// <summary>
/// What happened to one class of the subject's data.
/// </summary>
/// <remarks>
/// One row per class, and the vocabulary is closed. "Retained" on its own is the least useful
/// word in a privacy file, so anything that is not simply erased has to say on what basis — and
/// a retention has to say until when, because that date is the only thing that turns a policy
/// into something somebody can act on later.
/// </remarks>
public sealed class PrivacyOutcome : Entity
{
    private PrivacyOutcome()
    {
    }

    internal PrivacyOutcome(
        DataClass held,
        OutcomeKind kind,
        string? basis,
        DateOnly? until,
        Guid decidedById,
        DateTimeOffset at)
    {
        if (kind is OutcomeKind.Retained or OutcomeKind.Refused
            && string.IsNullOrWhiteSpace(basis))
        {
            throw new ArgumentException(
                "Say on what basis. A refusal or a retention with no reason is one nobody can "
                + "defend later, and a data subject is entitled to be told why.",
                nameof(basis));
        }

        if (kind == OutcomeKind.Retained && until is null)
        {
            throw new ArgumentException(
                "Say until when. A retention with no end is not a retention period, it is "
                + "keeping it for ever with a sentence attached.",
                nameof(until));
        }

        Held = held;
        Kind = kind;
        Basis = string.IsNullOrWhiteSpace(basis) ? null : basis.Trim();
        Until = until;
        DecidedById = decidedById;
        DecidedAt = at;
    }

    public DataClass Held { get; private init; }

    public OutcomeKind Kind { get; private init; }

    /// <summary>Why, in words the subject could be shown.</summary>
    public string? Basis { get; private init; }

    /// <summary>When a retention runs out. Null for anything else.</summary>
    public DateOnly? Until { get; private init; }

    public Guid DecidedById { get; private init; }

    public DateTimeOffset DecidedAt { get; private init; }
}

/// <summary>
/// A request made under the Data Protection Act about one person's data.
/// </summary>
/// <remarks>
/// Section 55.
///
/// <b>Erasure conflicts with nearly everything else this system does, and the conflict is the
/// section.</b> Leavers keep their name on their work; the audit trail is append-only and never
/// pruned; an incident timeline records who did what. All of that is deliberate, and a right to
/// erasure does not simply override it — Kenya's Data Protection Act, like the GDPR it resembles,
/// lets a controller keep what it is obliged to keep. What it does not allow is keeping things
/// quietly.
///
/// So the answer here is not a delete button. It is a record, per class of data, of what was done
/// and why — erased, anonymised, retained until a date on a stated basis, or refused with a
/// reason. A request cannot be closed with no outcomes recorded, because a closed request with an
/// empty list says a decision was taken and refuses to say what.
///
/// <b>The audit trail is never erased, for anybody, and that refusal is recorded rather than left
/// implicit.</b> It is the evidence that answers "who changed this", including changes made to
/// somebody's own data at their own request — so erasing it would destroy the proof that the
/// erasure happened. "We forgot" and "we decided" look identical in an empty file, which is why
/// it is written down as an outcome with a basis rather than simply not done.
/// </remarks>
public sealed class PrivacyRequest : Entity, IAuditable
{
    private readonly List<PrivacyOutcome> _outcomes = [];

    private PrivacyRequest()
    {
        Reference = string.Empty;
        Subject = string.Empty;
    }

    private PrivacyRequest(
        string reference,
        PrivacyAsk ask,
        SubjectKind subjectKind,
        Guid? subjectId,
        string subject,
        DateOnly receivedOn,
        DateTimeOffset at)
    {
        Reference = Require(reference, nameof(reference), 30);
        Ask = ask;
        SubjectKind = subjectKind;
        SubjectId = subjectId;
        Subject = Require(subject, nameof(subject), 200);
        ReceivedOn = receivedOn;
        RaisedAt = at;
    }

    public static PrivacyRequest Received(
        string reference,
        PrivacyAsk ask,
        SubjectKind subjectKind,
        string subject,
        DateOnly receivedOn,
        DateTimeOffset at,
        Guid? subjectId = null) =>
        new(reference, ask, subjectKind, subjectId, subject, receivedOn, at);

    public string Reference { get; private init; }

    public PrivacyAsk Ask { get; private init; }

    public SubjectKind SubjectKind { get; private init; }

    /// <summary>The staff or candidate record, when the subject is one.</summary>
    public Guid? SubjectId { get; private init; }

    /// <summary>Who they are, in words, so the request reads without a join.</summary>
    public string Subject { get; private init; }

    /// <summary>
    /// The day the request arrived.
    /// </summary>
    /// <remarks>
    /// Typed in rather than taken from the clock, because a request usually arrives by email or
    /// on paper and is entered later — and the statutory clock runs from when it was made, not
    /// from when somebody got round to recording it.
    /// </remarks>
    public DateOnly ReceivedOn { get; private set; }

    /// <summary>
    /// When an answer is owed.
    /// </summary>
    /// <remarks>
    /// Thirty days from receipt, which is the Data Protection Act's period. Computed rather than
    /// typed, because a deadline somebody types is one that can be typed wrong in the direction
    /// that suits whoever is typing.
    /// </remarks>
    public DateOnly DueOn => ReceivedOn.AddDays(30);

    public DateTimeOffset RaisedAt { get; private init; }

    public DateTimeOffset? AnsweredAt { get; private set; }

    public string? Note { get; private set; }

    public bool IsAnswered => AnsweredAt is not null;

    public IReadOnlyList<PrivacyOutcome> Outcomes =>
        [.. _outcomes.OrderBy(one => one.Held)];

    public bool IsOverdueOn(DateOnly today) => !IsAnswered && today > DueOn;

    /// <summary>How many days are left, negative once it is late.</summary>
    public int DaysLeftOn(DateOnly today) => DueOn.DayNumber - today.DayNumber;

    /// <summary>
    /// Record what was done about one class of data.
    /// </summary>
    /// <remarks>
    /// Replaces any earlier decision about the same class rather than adding a second, because
    /// two outcomes for one class is a file that answers the same question twice — and whichever
    /// a reader saw first would be whichever the database returned first.
    /// </remarks>
    public PrivacyOutcome Decide(
        DataClass held,
        OutcomeKind kind,
        Guid decidedById,
        DateTimeOffset at,
        string? basis = null,
        DateOnly? until = null)
    {
        if (IsAnswered)
        {
            throw new InvalidOperationException(
                $"{Reference} has been answered. The subject has been told what was decided, so "
                + "changing it now would make the answer they were given untrue. Record a new "
                + "request if they have asked again.");
        }

        if (held == DataClass.AuditTrail && kind is OutcomeKind.Erased or OutcomeKind.Anonymised)
        {
            throw new InvalidOperationException(
                "The audit trail is never erased or anonymised, for anybody. It is the evidence "
                + "that answers who changed what — including this erasure — so removing it would "
                + "destroy the proof that the request was honoured. Record it as retained, with "
                + "the basis.");
        }

        _outcomes.RemoveAll(one => one.Held == held);

        var outcome = new PrivacyOutcome(held, kind, basis, until, decidedById, at);

        _outcomes.Add(outcome);

        return outcome;
    }

    /// <summary>
    /// Tell the subject what was decided, and close it.
    /// </summary>
    /// <remarks>
    /// Refused with nothing recorded, because a closed request with an empty outcome list says a
    /// decision was taken and refuses to say what — which is exactly the state this whole record
    /// exists to prevent.
    /// </remarks>
    public void Answered(string note, DateTimeOffset at)
    {
        if (IsAnswered)
        {
            throw new InvalidOperationException($"{Reference} has already been answered.");
        }

        if (_outcomes.Count == 0)
        {
            throw new InvalidOperationException(
                "Nothing has been decided about any class of this person's data. A request "
                + "closed with an empty list says a decision was taken and refuses to say what.");
        }

        Note = Require(note, nameof(note), 4_000);
        AnsweredAt = at;

        Raise(new PrivacyRequestAnswered(Id, Reference, Ask, SubjectId, at));
    }

    public void Arrived(DateOnly receivedOn)
    {
        if (IsAnswered)
        {
            throw new InvalidOperationException(
                $"{Reference} has been answered, and the date it arrived is what the deadline "
                + "was computed from.");
        }

        ReceivedOn = receivedOn;
    }

    /// <summary>
    /// Nothing here is excluded from the trail.
    /// </summary>
    /// <remarks>
    /// Deliberately, and it is worth stating: the one file in this system that records decisions
    /// about somebody's privacy is the one that most needs to show who took them. The subject's
    /// own data is not in here — only the classes it falls into and what was decided.
    /// </remarks>
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

/// <summary>A data subject has been told what was decided.</summary>
public sealed record PrivacyRequestAnswered(
    Guid RequestId,
    string Reference,
    PrivacyAsk Ask,
    Guid? SubjectId,
    DateTimeOffset At) : DomainEvent;
