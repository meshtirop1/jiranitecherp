using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Time;

public enum LeaveKind
{
    Annual = 1,

    /// <summary>
    /// Taken after the fact, by definition.
    /// </summary>
    /// <remarks>
    /// The only kind that can be dated in the past. Nobody schedules being ill,
    /// and a system that refuses to record yesterday is one people stop using.
    /// </remarks>
    Sick = 2,

    Unpaid = 3,
    Compassionate = 4,
    Study = 5,

    /// <summary>
    /// Three months, under section 29 of the Employment Act.
    /// </summary>
    /// <remarks>
    /// Statutory, not discretionary, and the same is true of paternity leave
    /// below. A leave system for a Kenyan employer that does not name them
    /// forces the two entitlements every employee is certain to ask about into
    /// "annual" or "unpaid", which misstates the record and understates what the
    /// person is owed.
    /// </remarks>
    Maternity = 6,

    /// <summary>Two weeks, under the same section.</summary>
    Paternity = 7,
}

public enum LeaveStatus
{
    Draft = 1,
    AwaitingApproval = 2,
    Approved = 3,
    Refused = 4,

    /// <summary>Taken back, before or after approval.</summary>
    Cancelled = 5,
}

/// <summary>
/// Somebody asking to be away.
/// </summary>
/// <remarks>
/// Goes through the same approval engine as a requisition, by the same route: a
/// domain event opens a chain, and the answer comes back. Leave knows nothing
/// about who approves it, which is what lets that change without touching this.
/// </remarks>
public sealed class LeaveRequest : Entity, IAuditable
{
    private LeaveRequest()
    {
        Reason = string.Empty;
    }

    private LeaveRequest(
        Guid employeeId,
        LeaveKind kind,
        DateOnly from,
        DateOnly to,
        string reason,
        DateOnly today,
        IReadOnlySet<DateOnly> holidays)
    {
        if (to < from)
        {
            throw new ArgumentException("It cannot end before it starts.", nameof(to));
        }

        if (kind != LeaveKind.Sick && from < today)
        {
            /*
             * Sick leave excepted, because nobody schedules being ill.
             *
             * Everything else asked for in the past is either a mistake or
             * somebody covering an absence after the fact, and the second is a
             * conversation rather than a form.
             */
            throw new ArgumentException(
                "That starts in the past. Sick leave can be recorded afterwards; anything else "
                + "has to be asked for before it is taken.",
                nameof(from));
        }

        var days = WorkingDays.Between(from, to, holidays);

        if (days == 0)
        {
            /*
             * Refused rather than allowed through as a nought. A request that
             * costs nothing asks somebody to approve nothing, and it is not
             * harmless: the overlap check refuses a second request touching
             * these dates regardless of how long either one is, so a zero-day
             * request sitting on a week would block the real request for it.
             *
             * And it is nearly always one of two mistakes worth saying out
             * loud — a weekend somebody took for working days, or days that are
             * already public holidays. Either way the answer is that they do not
             * need to ask, which is better news than an approval queue entry.
             */
            throw new ArgumentException(
                "Every day in that range is a weekend or a public holiday, so there is no "
                + "working time in it to ask for.",
                nameof(from));
        }

        EmployeeId = employeeId;
        Kind = kind;
        From = from;
        To = to;
        Reason = Require(reason, nameof(reason));
        Status = LeaveStatus.Draft;
        Days = days;

        Raise(new LeaveAskedFor(Id, employeeId, kind, from, to, Days));
    }

    /// <summary>
    /// Ask for time off, against the holiday calendar as it stands today.
    /// </summary>
    /// <remarks>
    /// The calendar arrives as a parameter because the aggregate must not go
    /// looking for one. Whoever calls this has a database and can read it; this
    /// class has neither, and keeping it that way is what makes the count
    /// testable and the same twice.
    /// </remarks>
    public static LeaveRequest For(
        Guid employeeId,
        LeaveKind kind,
        DateOnly from,
        DateOnly to,
        string reason,
        DateOnly today,
        IReadOnlySet<DateOnly> holidays) =>
        new(employeeId, kind, from, to, reason, today, holidays);

    public Guid EmployeeId { get; private init; }

    public LeaveKind Kind { get; private init; }

    public DateOnly From { get; private init; }

    public DateOnly To { get; private init; }

    public string Reason { get; private set; }

    public LeaveStatus Status { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public string? Outcome { get; private set; }

    /// <summary>
    /// Working days, agreed when the request was made and kept.
    /// </summary>
    /// <remarks>
    /// This used to be computed from the dates on every read, which was correct
    /// and free for as long as weekends were the only thing taken off. Holidays
    /// broke it, because deciding whether 26 December is a working day needs a
    /// calendar, and an aggregate that reads a calendar out of a database is an
    /// aggregate that cannot be constructed in a test and answers differently
    /// depending on when you ask.
    ///
    /// Three ways out, and the trade is real in all of them. Making Days a
    /// method taking the calendar pushes the problem onto every caller,
    /// including the two screens and the events, and the day one caller passes
    /// an empty set — because it had nowhere to get one — the number is silently
    /// wrong and nothing fails. Moving the authoritative count to the service
    /// leaves the aggregate publishing a day count in its own events that
    /// disagrees with the one the rest of the system uses, which is worse than
    /// having no count on the aggregate at all. So: the figure is worked out once
    /// from the calendar as it stood, by the service that has it, and stored.
    ///
    /// What that costs is the one thing a computed property could never get
    /// wrong — this number can now disagree with the dates beside it. A holiday
    /// declared afterwards makes every stored count across it stale, and nothing
    /// in a row of leave says so. That is paid for in exactly one place:
    /// declaring or withdrawing a holiday recounts the live requests it touches,
    /// through <see cref="Recount"/>, in the same transaction. There is no second
    /// path that changes the calendar, and if one is ever added this is the
    /// invariant it has to honour.
    ///
    /// What it buys, beyond an aggregate with no database in it, is that the
    /// count can be read in SQL. The leave list had its own copy of the weekend
    /// rule for that reason and no longer does — it selects this column. One
    /// definition of the rule, one place it is applied, nothing left to drift.
    /// </remarks>
    public int Days { get; private set; }

    public bool IsLive => Status is LeaveStatus.Draft or LeaveStatus.AwaitingApproval
        or LeaveStatus.Approved;

    /// <summary>Does this overlap another request?</summary>
    public bool Overlaps(DateOnly from, DateOnly to) => From <= to && from <= To;

    /// <summary>
    /// Count it again, against a calendar that has changed.
    /// </summary>
    /// <remarks>
    /// So yes: a holiday declared inside leave that is already approved does
    /// shorten it, retroactively. That is the deliberate answer and it is worth
    /// the words, because the other answer is defensible too and this one costs
    /// something.
    ///
    /// What it costs is that a figure somebody has already seen changes under
    /// them. A leave balance reported in January can read differently in March
    /// with nobody having touched the request, and that is ordinarily the
    /// hallmark of a system nobody can reconcile. The reason it is acceptable
    /// here is that it cannot happen quietly: the recount runs in the same
    /// transaction as the change to the calendar, so the audit trail carries the
    /// holiday going on and this request's count coming down, next to each other,
    /// with one person's name on both.
    ///
    /// The case against freezing it is much harder to live with. The government
    /// announces a public holiday in this country with a week's notice and
    /// sometimes a day's, by which time leave over those dates is long approved.
    /// Freezing the count means the firm charges people for a day the country was
    /// shut, every time it happens, and the only way anybody gets it back is by
    /// noticing and asking — so the people who lose out are the ones who do not
    /// like to ask. A system that quietly keeps entitlement from the least
    /// assertive members of staff is not a defensible one, and a wrong number in
    /// an old report is the cheaper fault by a distance.
    ///
    /// Only while it is live. A refused or cancelled request charges nobody
    /// anything, so recounting one moves no entitlement and would put a row in
    /// the trail for every holiday ever declared across it — noise in the one
    /// place that must stay readable. Requests whose dates have already passed
    /// are still recounted, because the days were taken and the balance behind
    /// them is still the firm's to get right.
    /// </remarks>
    public void Recount(IReadOnlySet<DateOnly> holidays)
    {
        if (!IsLive)
        {
            throw new InvalidOperationException(
                $"This is {Status.ToString().ToLowerInvariant()}, so its length is settled and "
                + "recounting it would change a figure nobody is owed.");
        }

        // No event. The recount is a correction to a figure rather than
        // something happening to the leave, and a holiday across ten people's
        // Christmas would otherwise put ten messages on the outbox telling
        // everybody's approver about a day they already know is a holiday.
        Days = WorkingDays.Between(From, To, holidays);
    }

    public void Submit(DateTimeOffset at)
    {
        if (Status != LeaveStatus.Draft)
        {
            throw new InvalidOperationException(
                $"This is already {Status.ToString().ToLowerInvariant()}.");
        }

        Status = LeaveStatus.AwaitingApproval;

        Raise(new LeaveSubmitted(Id, EmployeeId, Kind, From, To, Days, at));
    }

    public void Approved(DateTimeOffset at)
    {
        Awaiting();

        Status = LeaveStatus.Approved;
        DecidedAt = at;
        Outcome = null;

        Raise(new LeaveApproved(Id, EmployeeId, Kind, From, To, Days, at));
    }

    public void Refused(string reason, DateTimeOffset at)
    {
        Awaiting();

        Status = LeaveStatus.Refused;
        DecidedAt = at;
        Outcome = Require(reason, nameof(reason));

        Raise(new LeaveRefused(Id, EmployeeId, Outcome, at));
    }

    /// <summary>
    /// Taken back.
    /// </summary>
    /// <remarks>
    /// Allowed after approval as well as before, because plans change and the
    /// alternative is somebody marked as away while sitting at their desk.
    /// </remarks>
    public void Cancel(DateTimeOffset at)
    {
        if (!IsLive)
        {
            throw new InvalidOperationException(
                $"This is already {Status.ToString().ToLowerInvariant()}.");
        }

        Status = LeaveStatus.Cancelled;
        DecidedAt = at;

        Raise(new LeaveCancelled(Id, EmployeeId, From, To, at));
    }

    /// <summary>
    /// The reason is between somebody and their manager, and it is in the trail
    /// because approving leave is a decision somebody answers for.
    /// </summary>
    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private void Awaiting()
    {
        if (Status != LeaveStatus.AwaitingApproval)
        {
            throw new InvalidOperationException(
                $"This is {Status.ToString().ToLowerInvariant()}, so there is no decision "
                + "outstanding on it.");
        }
    }

    private static string Require(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();
}

public sealed record LeaveAskedFor(
    Guid LeaveId,
    Guid EmployeeId,
    LeaveKind Kind,
    DateOnly From,
    DateOnly To,
    int Days) : DomainEvent;

/// <summary>Sent for approval. This is what opens the chain.</summary>
public sealed record LeaveSubmitted(
    Guid LeaveId,
    Guid EmployeeId,
    LeaveKind Kind,
    DateOnly From,
    DateOnly To,
    int Days,
    DateTimeOffset At) : DomainEvent;

public sealed record LeaveApproved(
    Guid LeaveId,
    Guid EmployeeId,
    LeaveKind Kind,
    DateOnly From,
    DateOnly To,
    int Days,
    DateTimeOffset At) : DomainEvent;

public sealed record LeaveRefused(
    Guid LeaveId, Guid EmployeeId, string Reason, DateTimeOffset At) : DomainEvent;

public sealed record LeaveCancelled(
    Guid LeaveId, Guid EmployeeId, DateOnly From, DateOnly To, DateTimeOffset At) : DomainEvent;
