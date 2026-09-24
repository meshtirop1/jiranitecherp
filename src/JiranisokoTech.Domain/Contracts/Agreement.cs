using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Contracts;

/// <summary>
/// What sort of paper this is.
/// </summary>
/// <remarks>
/// Deliberately not including a client contract. That is <see cref="Contract"/>, and it is a
/// different thing in every way that matters: it carries a value, it is what invoices are raised
/// against, and it is the firm's revenue. The paper here carries obligations and no money —
/// which is why one table would have meant a nullable value column and a screen apologising for
/// it on half the rows.
/// </remarks>
public enum AgreementKind
{
    /// <summary>Somebody's contract of employment.</summary>
    Employment = 1,

    /// <summary>What the firm has agreed to with a supplier.</summary>
    Vendor = 2,

    /// <summary>A confidentiality agreement, either way round.</summary>
    Nda = 3,

    /// <summary>A licence, a lease, a data-processing agreement, anything else signed.</summary>
    Other = 4,
}

/// <summary>
/// Where a piece of paper has got to.
/// </summary>
/// <remarks>
/// Four, and <see cref="Superseded"/> is the one worth having. An employment contract replaced
/// by a new one after a pay rise is not terminated and did not expire — it was replaced, and the
/// difference is exactly what somebody reading a personnel file six months later needs to see.
/// </remarks>
public enum AgreementState
{
    /// <summary>Written, not signed by everybody yet.</summary>
    Draft = 1,

    /// <summary>Signed, and in force.</summary>
    Signed = 2,

    /// <summary>Replaced by a later one.</summary>
    Superseded = 3,

    /// <summary>Ended before its time, by somebody's decision.</summary>
    Ended = 4,
}

/// <summary>
/// Paper the firm has signed with somebody who is not a client.
/// </summary>
/// <remarks>
/// Section 17's second half. Client contracts had been here since the section was built;
/// employment contracts, vendor agreements and NDAs had not, so the only place an employment
/// contract existed was as a file attached to a staff record, with nothing knowing when it ran
/// out.
///
/// <b>The counterparty is either a member of staff or a name.</b> An employment contract is with
/// somebody on the staff list and that link is worth having — it is what puts the contract on
/// their record. A vendor agreement is with a company this system does not otherwise know about,
/// and inventing a supplier table to hold a name would be section 62's work done badly in
/// passing. So one nullable link and one required name, and the name is filled in from the staff
/// record when there is a link, because a page showing a blank party is a page nobody trusts.
///
/// <b>Renewal is the reason this exists at all.</b> An agreement with no end date is a note; one
/// with an end date is a deadline, and the job that reads it is what turns a filing cabinet into
/// a system. It rides the same reminder ladder as client contracts.
///
/// <b>Nothing here is a signature.</b> The signed copy is a document attached to it, exactly as
/// a client contract's is. Recording that somebody signed is not the same as them having signed,
/// and this system does not pretend to the second.
/// </remarks>
public sealed class Agreement : Entity, IAuditable
{
    private Agreement()
    {
        Reference = string.Empty;
        Title = string.Empty;
        Party = string.Empty;
    }

    private Agreement(
        AgreementKind kind,
        string reference,
        string title,
        string party,
        Guid? employeeId,
        DateTimeOffset at)
    {
        Kind = kind;
        Reference = Required(reference, nameof(reference));
        Title = Required(title, nameof(title));
        Party = Required(party, nameof(party));
        EmployeeId = employeeId;
        State = AgreementState.Draft;
        DraftedAt = at;
    }

    public static Agreement Draft(
        AgreementKind kind,
        string reference,
        string title,
        string party,
        DateTimeOffset at,
        Guid? employeeId = null) =>
        new(kind, reference, title, party, employeeId, at);

    public AgreementKind Kind { get; private set; }

    /// <summary>The firm's own file number for it.</summary>
    /// <remarks>
    /// Unique, like a client contract's, because it is what somebody quotes in an email — and
    /// two pieces of paper answering to JTS-NDA-2026-004 make every reference to it ambiguous.
    /// </remarks>
    public string Reference { get; private init; }

    public string Title { get; private set; }

    /// <summary>Who it is with, in words.</summary>
    public string Party { get; private set; }

    /// <summary>The staff record it belongs to, when it is one of theirs.</summary>
    public Guid? EmployeeId { get; private set; }

    public AgreementState State { get; private set; }

    public DateOnly? StartsOn { get; private set; }

    /// <summary>
    /// When it runs out, when it does.
    /// </summary>
    /// <remarks>
    /// Optional, because a great many agreements are open-ended — an NDA that runs until it is
    /// terminated, a permanent contract of employment. Where it is set it is the only field
    /// here anything reads on its own.
    /// </remarks>
    public DateOnly? EndsOn { get; private set; }

    /// <summary>
    /// How much notice either side has to give.
    /// </summary>
    /// <remarks>
    /// Recorded because it is the question actually asked of an agreement that has no end date:
    /// not "when does this stop" but "how long does it take to stop it".
    /// </remarks>
    public int? NoticeDays { get; private set; }

    public DateTimeOffset DraftedAt { get; private init; }

    public DateTimeOffset? SignedAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    /// <summary>Why it ended, or what replaced it.</summary>
    public string? Outcome { get; private set; }

    public string? Notes { get; private set; }

    public bool IsInForce => State == AgreementState.Signed;

    public bool HasExpiredOn(DateOnly today) =>
        IsInForce && EndsOn is { } ends && today > ends;

    /// <summary>Is this still something somebody should be reminded about?</summary>
    public bool IsLive => State is AgreementState.Draft or AgreementState.Signed;

    public void Describe(
        AgreementKind kind, string title, string party, Guid? employeeId, string? notes)
    {
        Kind = kind;
        Title = Required(title, nameof(title));
        Party = Required(party, nameof(party));
        EmployeeId = employeeId;
        Notes = Trimmed(notes);
    }

    /// <summary>
    /// When it runs, and how much notice ending it takes.
    /// </summary>
    /// <remarks>
    /// Refused when the end is before the start, which is the typo a date field invites — and
    /// an agreement that ended before it began would sit in the expired list for ever with
    /// nobody able to say what it was.
    /// </remarks>
    public void Runs(DateOnly? startsOn, DateOnly? endsOn, int? noticeDays)
    {
        if (startsOn is { } from && endsOn is { } to && to < from)
        {
            throw new ArgumentException(
                "It cannot end before it starts.", nameof(endsOn));
        }

        if (noticeDays is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(noticeDays), "Notice cannot be a negative number of days.");
        }

        StartsOn = startsOn;
        EndsOn = endsOn;
        NoticeDays = noticeDays;
    }

    /// <summary>
    /// Everybody has signed it.
    /// </summary>
    /// <remarks>
    /// A date is not required to sign. Plenty of agreements are signed before anybody decides
    /// when they start, and a system that refused would have somebody type a date they did not
    /// mean in order to record a thing that had happened.
    /// </remarks>
    public void Signed(DateTimeOffset at)
    {
        if (State != AgreementState.Draft)
        {
            throw new InvalidOperationException(
                State == AgreementState.Signed
                    ? "This is already signed."
                    : "This is no longer in force, so it cannot be signed.");
        }

        State = AgreementState.Signed;
        SignedAt = at;

        Raise(new AgreementSigned(Id, Kind, Reference, Party, EndsOn, at));
    }

    /// <summary>
    /// A later one has taken its place.
    /// </summary>
    /// <remarks>
    /// Kept rather than deleted, which is the whole point of having this state. An employment
    /// contract replaced after a pay rise is the evidence of what somebody was on before, and
    /// deleting it would leave a personnel file that begins in the middle.
    /// </remarks>
    public void SupersededBy(string reference, DateTimeOffset at)
    {
        if (!IsLive)
        {
            throw new InvalidOperationException("This agreement is already over.");
        }

        State = AgreementState.Superseded;
        EndedAt = at;
        Outcome = $"Replaced by {Required(reference, nameof(reference))}";
    }

    public void End(string why, DateTimeOffset at)
    {
        if (!IsLive)
        {
            throw new InvalidOperationException("This agreement is already over.");
        }

        State = AgreementState.Ended;
        EndedAt = at;
        Outcome = Required(why, nameof(why));
    }

    /// <summary>
    /// Nothing about this paper is excluded from the trail, and that is deliberate.
    /// </summary>
    /// <remarks>
    /// An employment contract's salary is not here — it is on the staff record, where it is
    /// excluded — and the rest of what is on this row is who signed what and when, which is
    /// exactly what an audit trail is for. The signed document itself is an attachment, behind
    /// the same permission a personnel file is.
    /// </remarks>
    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private static string Required(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// A piece of paper has been signed.
/// </summary>
/// <remarks>
/// Carries the end date, because the obvious thing to do with this is diarise the renewal — and
/// a subscriber that had to come back and read the row would be reading it minutes later, from
/// the outbox, by which time somebody may have changed it.
/// </remarks>
public sealed record AgreementSigned(
    Guid AgreementId,
    AgreementKind Kind,
    string Reference,
    string Party,
    DateOnly? EndsOn,
    DateTimeOffset At) : DomainEvent;
