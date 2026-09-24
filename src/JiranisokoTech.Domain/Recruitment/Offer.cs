using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Domain.People;

namespace JiranisokoTech.Domain.Recruitment;

using Money = JiranisokoTech.Domain.Common.Money;

/// <summary>
/// Where an offer has got to.
/// </summary>
/// <remarks>
/// Five, and there is deliberately no sixth for "expired". Whether an offer has run out is a
/// comparison between its closing date and today, and storing it as a state would mean a job
/// somewhere flipping rows every night — so an offer nobody got round to would read as open
/// until that job next ran, and the one screen that has to be right about it would be wrong for
/// as long as the job was broken.
/// </remarks>
public enum OfferStatus
{
    /// <summary>Written and not yet sent. Nobody outside the firm has seen it.</summary>
    Drafted = 1,

    /// <summary>With the candidate, waiting.</summary>
    Sent = 2,

    Accepted = 3,

    Declined = 4,

    /// <summary>Taken back by the firm.</summary>
    Withdrawn = 5,
}

/// <summary>
/// What the firm is offering somebody, and what they said.
/// </summary>
/// <remarks>
/// Section 8, and the step section 92's hiring chain stopped at: the requisition, the advert,
/// the application, the interviews and the assessment all existed, and then the record ended
/// with a status called Offered and nothing saying what was offered.
///
/// <b>The terms are on the record because the email deliberately does not carry them.</b>
/// <c>Letters.Offer</c> says an offer is coming and that the terms follow separately, on the
/// grounds that an email reading like a contract is an email somebody will later say they
/// accepted. That was the right call and it left a promise nobody kept — this is the thing that
/// keeps it.
///
/// <b>Acceptance is a link with a secret in it, not an account.</b> A candidate has no sign-in
/// and should not need one to answer; making them create an account to accept a job is the kind
/// of friction that loses people at the last step. The secret is 256 bits from the operating
/// system's generator and only its hash is kept here, exactly as an API key is, so the database
/// does not hold anything that would let somebody accept on a candidate's behalf.
///
/// <b>Accepting creates nothing.</b> No employee, no onboarding, no account. Somebody presses a
/// button afterwards, and that is the same reasoning as an opportunity being won creating no
/// client, no project and no contract: they are separate decisions with separate consequences,
/// and a start date that moves by a fortnight between acceptance and arrival is the ordinary
/// case rather than the exception.
/// </remarks>
public sealed class Offer : Entity, IAuditable
{
    private Offer()
    {
        JobTitle = string.Empty;
        Terms = string.Empty;
        TokenHash = string.Empty;
    }

    private Offer(
        Guid applicationId,
        string jobTitle,
        Money salary,
        PayFrequency frequency,
        DateOnly startsOn,
        DateOnly closesOn,
        string terms,
        string tokenHash,
        Guid madeById,
        DateTimeOffset at)
    {
        if (closesOn < DateOnly.FromDateTime(at.UtcDateTime))
        {
            throw new ArgumentException(
                "An offer cannot close before it is written.", nameof(closesOn));
        }

        ApplicationId = applicationId;
        JobTitle = Required(jobTitle, nameof(jobTitle));
        SalaryMinorUnits = salary.MinorUnits;
        SalaryCurrency = salary.Currency;
        Frequency = frequency;
        StartsOn = startsOn;
        ClosesOn = closesOn;
        Terms = terms?.Trim() ?? string.Empty;
        TokenHash = Required(tokenHash, nameof(tokenHash));
        MadeById = madeById;
        MadeAt = at;
        Status = OfferStatus.Drafted;
    }

    public static Offer Write(
        Guid applicationId,
        string jobTitle,
        Money salary,
        PayFrequency frequency,
        DateOnly startsOn,
        DateOnly closesOn,
        string terms,
        string tokenHash,
        Guid madeById,
        DateTimeOffset at) =>
        new(applicationId, jobTitle, salary, frequency, startsOn, closesOn, terms, tokenHash,
            madeById, at);

    public Guid ApplicationId { get; private init; }

    public string JobTitle { get; private set; }

    /// <summary>
    /// The salary, stored the way every other amount in this system is.
    /// </summary>
    /// <remarks>
    /// Minor units beside a currency, never a decimal. Three money boxes in this application
    /// once stored a hundredth of what was typed, and the reason was a decimal going somewhere
    /// that wanted minor units.
    /// </remarks>
    public long SalaryMinorUnits { get; private set; }

    public string SalaryCurrency { get; private set; } = "KES";

    public Money Salary => Money.Of(SalaryMinorUnits, SalaryCurrency);

    /// <summary>Whether that figure is a month, a year or a day.</summary>
    /// <remarks>
    /// On the offer rather than assumed, because the number means nothing without it and
    /// because getting it wrong by a factor of twelve is the single most expensive mistake this
    /// screen could make.
    /// </remarks>
    public PayFrequency Frequency { get; private set; }

    public DateOnly StartsOn { get; private set; }

    /// <summary>
    /// The last day it can be accepted.
    /// </summary>
    /// <remarks>
    /// Required rather than optional. An offer with no closing date is one the firm cannot plan
    /// around and the candidate cannot read as urgent, and it is the reason requisitions sit
    /// open for months with a post neither filled nor available.
    /// </remarks>
    public DateOnly ClosesOn { get; private set; }

    /// <summary>Probation, notice, hours, holiday — in the firm's own words.</summary>
    /// <remarks>
    /// Free text rather than fields, deliberately. Employment terms differ by role and by
    /// country, and a form with eight boxes would force every offer through whichever eight the
    /// first one needed. What matters here is that whatever was said is kept, and that the
    /// candidate saw exactly this text when they accepted.
    /// </remarks>
    public string Terms { get; private set; }

    /// <summary>The hash of the acceptance secret. The secret itself is not kept.</summary>
    public string TokenHash { get; private init; }

    public OfferStatus Status { get; private set; }

    public Guid MadeById { get; private init; }

    public DateTimeOffset MadeAt { get; private init; }

    public DateTimeOffset? SentAt { get; private set; }

    public DateTimeOffset? AnsweredAt { get; private set; }

    /// <summary>
    /// What the candidate typed as their name when they answered.
    /// </summary>
    /// <remarks>
    /// This is the electronic signature, and it is worth being honest about what it is worth: a
    /// name typed into a box by whoever held the link. It is not a qualified signature and this
    /// system does not pretend otherwise. What makes it evidence is the rest of the record — a
    /// secret only sent to one address, the moment it was used, and the terms frozen at the
    /// moment they were agreed to.
    /// </remarks>
    public string? SignedName { get; private set; }

    /// <summary>Why it was declined or withdrawn, when anybody said.</summary>
    public string? Outcome { get; private set; }

    /// <summary>
    /// The staff record this became, once somebody made one.
    /// </summary>
    /// <remarks>
    /// A column rather than a table for the link, because it is one-to-one and nullable — most
    /// offers never become anybody, and the ones that do become exactly one person. What it is
    /// for is refusing the second press of the button: a staff record made twice from one offer
    /// is two people with one name, one of whom will be paid.
    /// </remarks>
    public Guid? EmployeeId { get; private set; }

    public bool IsOut => Status == OfferStatus.Sent;

    public bool IsSettled => Status is OfferStatus.Accepted or OfferStatus.Declined;

    /// <summary>
    /// Has an offer that is still out run past its closing date?
    /// </summary>
    /// <remarks>
    /// Computed, never stored — see the note on <see cref="OfferStatus"/>. It takes the date
    /// rather than reading a clock so that the answer is the same everywhere it is asked.
    /// </remarks>
    public bool HasLapsed(DateOnly today) => IsOut && today > ClosesOn;

    /// <summary>Can somebody holding the link still answer?</summary>
    public bool CanAnswer(DateOnly today) => IsOut && !HasLapsed(today);

    /// <summary>Correct it while nobody outside the firm has seen it.</summary>
    public void Revise(
        string jobTitle,
        Money salary,
        PayFrequency frequency,
        DateOnly startsOn,
        DateOnly closesOn,
        string terms)
    {
        RefuseUnless(OfferStatus.Drafted, "changed");

        JobTitle = Required(jobTitle, nameof(jobTitle));
        SalaryMinorUnits = salary.MinorUnits;
        SalaryCurrency = salary.Currency;
        Frequency = frequency;
        StartsOn = startsOn;
        ClosesOn = closesOn;
        Terms = terms?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// It has gone to the candidate.
    /// </summary>
    /// <remarks>
    /// The terms stop being editable here rather than at acceptance, which is the point of
    /// having a draft state at all: the candidate is reading a page, and a page that can change
    /// under them between reading and accepting is not an offer anybody could rely on.
    /// </remarks>
    public void Sent(DateTimeOffset at)
    {
        RefuseUnless(OfferStatus.Drafted, "sent");

        Status = OfferStatus.Sent;
        SentAt = at;

        Raise(new OfferSent(Id, ApplicationId, JobTitle, ClosesOn, at));
    }

    /// <summary>
    /// The candidate said yes.
    /// </summary>
    /// <remarks>
    /// Refused after the closing date, and the refusal is deliberate rather than generous. An
    /// offer accepted three weeks late is one the firm may well still want — but that is a
    /// decision somebody makes, by extending the date and saying so, rather than something a
    /// candidate does by finding an old email.
    /// </remarks>
    public void Accepted(string signedName, DateOnly today, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(signedName))
        {
            throw new ArgumentException(
                "Type your name to accept.", nameof(signedName));
        }

        RefuseUnlessAnswerable(today, "accepted");

        Status = OfferStatus.Accepted;
        SignedName = signedName.Trim();
        AnsweredAt = at;

        Raise(new OfferAccepted(Id, ApplicationId, JobTitle, StartsOn, at));
    }

    /// <summary>
    /// The candidate said no.
    /// </summary>
    /// <remarks>
    /// The reason is optional, unlike a rollback's or a withdrawal's. Somebody turning down a
    /// job owes the firm nothing, and a required box here would be answered with a full stop.
    /// </remarks>
    public void Declined(string? why, DateOnly today, DateTimeOffset at)
    {
        RefuseUnlessAnswerable(today, "declined");

        Status = OfferStatus.Declined;
        Outcome = Trimmed(why);
        AnsweredAt = at;

        Raise(new OfferDeclined(Id, ApplicationId, JobTitle, Outcome, at));
    }

    /// <summary>
    /// The firm takes it back.
    /// </summary>
    /// <remarks>
    /// Reachable from drafted as well as sent, because an offer written and thought better of
    /// should leave the same record as one that went out — the requisition it was against is the
    /// same requisition either way, and a deleted row explains nothing to whoever asks next
    /// month why that post is still open.
    /// </remarks>
    public void Withdraw(string why, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(why))
        {
            throw new ArgumentException(
                "Say why it is being withdrawn. Somebody was told they had this.", nameof(why));
        }

        if (IsSettled)
        {
            throw new InvalidOperationException(
                Status == OfferStatus.Accepted
                    ? "This offer was accepted. Taking it back afterwards is not something this "
                      + "system will record as a withdrawal — it is a conversation, and then a "
                      + "decision on the staff record."
                    : "This offer was already declined.");
        }

        Status = OfferStatus.Withdrawn;
        Outcome = why.Trim();
        AnsweredAt = at;
    }

    /// <summary>
    /// Record which staff record was made from this.
    /// </summary>
    /// <remarks>
    /// Refused twice over: once here, so the aggregate cannot be talked into it, and once by the
    /// service before it creates anything. The second is what produces a sentence somebody can
    /// read; this is what makes it true.
    /// </remarks>
    public void Became(Guid employeeId)
    {
        if (Status != OfferStatus.Accepted)
        {
            throw new InvalidOperationException(
                "Only an accepted offer becomes a member of staff.");
        }

        if (EmployeeId is not null)
        {
            throw new InvalidOperationException(
                "A staff record has already been made from this offer.");
        }

        EmployeeId = employeeId;
    }

    /// <summary>
    /// Give somebody longer.
    /// </summary>
    /// <remarks>
    /// Its own method rather than part of <see cref="Revise"/>, because it is the one change a
    /// sent offer is allowed. Everything else about a sent offer is what the candidate is
    /// reading; the closing date is the one thing that can only be moved in their favour.
    /// </remarks>
    public void CloseOn(DateOnly closesOn)
    {
        if (!IsOut)
        {
            throw new InvalidOperationException(
                "Only an offer that is still out can be given longer.");
        }

        if (closesOn < ClosesOn)
        {
            throw new ArgumentException(
                "The closing date can be moved out, not in. Bringing it forward would withdraw "
                + "an offer somebody is still reading without saying so.",
                nameof(closesOn));
        }

        ClosesOn = closesOn;
    }

    /// <summary>
    /// The salary and the signature stay out of the trail.
    /// </summary>
    /// <remarks>
    /// The same decision as <c>Employee.AuditExcludes</c>, and for the same reason: the trail is
    /// append-only and never pruned, so a figure in it is a figure kept forever under rather
    /// different access rules from the screen it was typed into. That an offer was written,
    /// sent, accepted or withdrawn is the act, and the act is what is recorded.
    /// </remarks>
    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>
    {
        nameof(SalaryMinorUnits),
        nameof(Terms),
        nameof(SignedName),
        nameof(TokenHash),
    };

    private void RefuseUnlessAnswerable(DateOnly today, string verb)
    {
        if (Status == OfferStatus.Drafted)
        {
            throw new InvalidOperationException(
                "This offer has not been sent, so it cannot be " + verb + ".");
        }

        if (IsSettled || Status == OfferStatus.Withdrawn)
        {
            throw new InvalidOperationException("This offer has already been answered.");
        }

        if (HasLapsed(today))
        {
            throw new InvalidOperationException(
                $"This offer closed on {ClosesOn:d MMMM yyyy}. Ask whoever you have been "
                + "speaking to — they can give you longer.");
        }
    }

    private void RefuseUnless(OfferStatus required, string verb)
    {
        if (Status == required)
        {
            return;
        }

        var said = Status switch
        {
            OfferStatus.Drafted => "has not been sent",
            OfferStatus.Sent => "is with the candidate",
            OfferStatus.Accepted => "has been accepted",
            OfferStatus.Declined => "was declined",
            _ => "was withdrawn",
        };

        throw new InvalidOperationException($"This offer {said}, so it cannot be {verb}.");
    }

    private static string Required(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// An offer is with somebody.
/// </summary>
/// <remarks>
/// No salary on any of these three events, deliberately, for the reason
/// <c>EmployeeTermsChanged</c> carries none: an event is serialised into the outbox and read
/// back by whatever is listening, and the outbox is not the place to keep what somebody is paid.
/// </remarks>
public sealed record OfferSent(
    Guid OfferId,
    Guid ApplicationId,
    string JobTitle,
    DateOnly ClosesOn,
    DateTimeOffset At) : DomainEvent;

public sealed record OfferAccepted(
    Guid OfferId,
    Guid ApplicationId,
    string JobTitle,
    DateOnly StartsOn,
    DateTimeOffset At) : DomainEvent;

public sealed record OfferDeclined(
    Guid OfferId,
    Guid ApplicationId,
    string JobTitle,
    string? Why,
    DateTimeOffset At) : DomainEvent;
