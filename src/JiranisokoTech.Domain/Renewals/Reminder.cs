using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Renewals;

/// <summary>
/// What a reminder is about.
/// </summary>
/// <remarks>
/// Numbered explicitly because these are stored as integers, and a member inserted in the
/// middle of an unnumbered enum renumbers every row already written.
/// </remarks>
public enum ReminderKind
{
    ContractRenewal = 1,
    QualificationLapsing = 2,
    InvoiceOverdue = 3,

    /// <summary>A domain, a certificate or a subscription running out. Section 14.</summary>
    ResourceExpiring = 4,
}

/// <summary>
/// How close a deadline has got.
/// </summary>
/// <remarks>
/// Three, and the point of having three at all is that one notice is not enough and
/// forty-five are worse than none. A reminder sent once at ninety days is forgotten by the
/// time it matters; a reminder sent every day for forty-five days is a filter rule.
/// </remarks>
public enum ReminderStage
{
    First = 1,
    Second = 2,
    Final = 3,
}

/// <summary>
/// When to speak up about an approaching deadline, and how loudly.
/// </summary>
/// <remarks>
/// This exists because of a fault in the two jobs that were already written. Each of them
/// asked "what expires within the next N days" and mailed everybody about all of it, every
/// single morning. A contract ending in forty-five days therefore produced forty-five
/// identical emails to every department head, and a qualification lapsing in two months
/// produced about sixty — which is not a reminder system, it is the reason somebody makes a
/// rule that files mail from this address into a folder they never open. Both jobs declare
/// <c>IRecurringJob</c>, whose contract says in as many words that every job must be safe to
/// run twice and that running twice finds nothing the second time. Neither was.
///
/// A ladder replaces the window. There are exactly three moments at which telling somebody
/// changes what they do, and the gaps between them are the whole design: far enough out to
/// start a conversation, close enough to chase it, and late enough that somebody has to act
/// today.
/// </remarks>
public readonly record struct ReminderLadder
{
    private ReminderLadder(int first, int second, int final)
    {
        FirstDaysBefore = first;
        SecondDaysBefore = second;
        FinalDaysBefore = final;
    }

    public int FirstDaysBefore { get; }

    public int SecondDaysBefore { get; }

    public int FinalDaysBefore { get; }

    /// <summary>
    /// Three thresholds, strictly descending.
    /// </summary>
    /// <remarks>
    /// Each is a number of days until the deadline, so it goes negative once the deadline has
    /// passed — which is not a trick, it is what an overdue invoice is. An invoice's ladder is
    /// -7, -21, -45: a week late, three weeks late, six weeks late. Nothing here refuses a
    /// negative threshold, and an earlier version did, which made the only kind of deadline
    /// anybody actually chases impossible to express.
    ///
    /// What is refused is thresholds that do not come strictly closer together, because two
    /// equal ones mean a stage can never be reached — the earlier always matches first — and
    /// a stage that cannot be reached is a reminder somebody is counting on and will not get.
    /// Ascending is the same fault with the stages in the wrong order, which reads as working
    /// right until the day it matters.
    /// </remarks>
    public static ReminderLadder Of(int firstDaysBefore, int secondDaysBefore, int finalDaysBefore)
    {
        if (firstDaysBefore <= secondDaysBefore || secondDaysBefore <= finalDaysBefore)
        {
            throw new ArgumentException(
                "The three thresholds have to come strictly closer together — "
                + $"{firstDaysBefore}, then {secondDaysBefore}, then {finalDaysBefore}. Two that "
                + "are equal make one stage unreachable, and nobody notices a reminder that "
                + "never arrives.",
                nameof(secondDaysBefore));
        }

        return new ReminderLadder(firstDaysBefore, secondDaysBefore, finalDaysBefore);
    }

    /// <summary>
    /// The ladder for each kind of deadline.
    /// </summary>
    /// <remarks>
    /// Written here rather than in the jobs, because the numbers are the policy and the job
    /// is the plumbing. They differ because the work they provoke differs: renewing a client
    /// contract is a negotiation and wants three months, sitting an examination wants two,
    /// and an invoice that is already late wants chasing on a fortnightly rhythm rather than
    /// a quarterly one.
    /// </remarks>
    public static ReminderLadder For(ReminderKind kind) => kind switch
    {
        ReminderKind.ContractRenewal => Of(90, 45, 14),
        ReminderKind.QualificationLapsing => Of(60, 30, 7),

        /*
         * Counted forwards from the due date rather than backwards towards it, which is why
         * the numbers are negative: an invoice's deadline has already passed by the time
         * anybody is chasing it. Seven, twenty-one and forty-five days late.
         */
        ReminderKind.InvoiceOverdue => Of(-7, -21, -45),

        /*
         * Tighter than a contract's, because the failure is different in kind. A contract that
         * lapses is a conversation; a certificate that expires takes the site down at a moment
         * nobody chose, and a domain that lapses takes the firm's email with it. Thirty days is
         * enough to renew anything, and seven and two are the ones that get somebody out of a
         * meeting.
         */
        ReminderKind.ResourceExpiring => Of(30, 7, 2),
        _ => Of(90, 45, 14),
    };

    /// <summary>
    /// Which stage falls due today, if any.
    /// </summary>
    /// <remarks>
    /// Returns the nearest threshold that today has reached, and nothing at all until the
    /// first one is reached. The nearest rather than the furthest: a job that has been off
    /// for a fortnight and comes back with the deadline eleven days away should send the
    /// notice that is true now, not the one that was true when it stopped. Sending the
    /// ninety-day letter about something due in eleven days is worse than sending nothing.
    /// </remarks>
    public ReminderStage? StageDueOn(DateOnly today, DateOnly deadline)
    {
        var away = deadline.DayNumber - today.DayNumber;

        if (away <= FinalDaysBefore)
        {
            return ReminderStage.Final;
        }

        if (away <= SecondDaysBefore)
        {
            return ReminderStage.Second;
        }

        return away <= FirstDaysBefore ? ReminderStage.First : null;
    }
}

/// <summary>
/// A notice that was given, recorded so that it is not given again.
/// </summary>
/// <remarks>
/// The fact nothing in this system could hold. Both scheduled mail jobs were written to be
/// safe run twice — their interface demands it in as many words — and neither was, because
/// "have we already told somebody about this deadline" had no answer anywhere. This is the
/// answer: one row per notice, and the unique index across kind, subject, deadline and stage
/// is what makes the second run of the day find it and send nothing.
///
/// <b>The deadline is part of the identity.</b> Not only the subject and the stage. A
/// contract whose end date is extended has a new deadline, and the firm genuinely should be
/// warned again about the new one — keying on the subject alone would silence every reminder
/// for a contract that had ever been warned about once.
///
/// <b>Not IAuditable.</b> Deliberately, and it is the one departure worth stating. This table
/// grows by a handful of rows a week for ever and nothing about it is a decision somebody
/// took — it is a record that a machine sent an email. Putting it in the audit trail would
/// bury the changes people actually made, which is the argument the trail's own filters exist
/// to win.
/// </remarks>
public sealed class Reminder : Entity
{
    private Reminder()
    {
        Subject = string.Empty;
    }

    private Reminder(
        ReminderKind kind,
        Guid subjectId,
        string subject,
        ReminderStage stage,
        DateOnly deadlineOn,
        int told,
        DateTimeOffset at)
    {
        if (subjectId == Guid.Empty)
        {
            throw new ArgumentException(
                "A reminder has to be about something. Without the subject's identifier this "
                + "row cannot stop a second notice, which is the only reason it is written.",
                nameof(subjectId));
        }

        if (told <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(told),
                "A reminder nobody was told about is not a reminder. Recording one would "
                + "silence the notice that should have gone out tomorrow.");
        }

        Kind = kind;
        SubjectId = subjectId;
        Subject = Required(subject, nameof(subject));
        Stage = stage;
        DeadlineOn = deadlineOn;
        Told = told;
        At = at;
    }

    public static Reminder Issued(
        ReminderKind kind,
        Guid subjectId,
        string subject,
        ReminderStage stage,
        DateOnly deadlineOn,
        int told,
        DateTimeOffset at) =>
        new(kind, subjectId, subject, stage, deadlineOn, told, at);

    public ReminderKind Kind { get; private init; }

    /// <summary>The contract, the certification's holder, or the invoice.</summary>
    public Guid SubjectId { get; private init; }

    /// <summary>
    /// What it was about, in words, as it read on the day.
    /// </summary>
    /// <remarks>
    /// Kept rather than looked up when the history is read. A contract that has since been
    /// retitled, or an invoice long since deleted, would otherwise make the record of what
    /// was said unreadable — and the only question this table is ever asked is "what did we
    /// tell them, and when".
    /// </remarks>
    public string Subject { get; private init; }

    public ReminderStage Stage { get; private init; }

    /// <summary>The deadline this was about, which is part of what makes it unique.</summary>
    public DateOnly DeadlineOn { get; private init; }

    /// <summary>How many people were told.</summary>
    public int Told { get; private init; }

    public DateTimeOffset At { get; private init; }

    private static string Required(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();
}
