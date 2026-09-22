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
        Guid employeeId, LeaveKind kind, DateOnly from, DateOnly to, string reason, DateOnly today)
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

        EmployeeId = employeeId;
        Kind = kind;
        From = from;
        To = to;
        Reason = Require(reason, nameof(reason));
        Status = LeaveStatus.Draft;

        Raise(new LeaveAskedFor(Id, employeeId, kind, from, to, Days));
    }

    public static LeaveRequest For(
        Guid employeeId,
        LeaveKind kind,
        DateOnly from,
        DateOnly to,
        string reason,
        DateOnly today) =>
        new(employeeId, kind, from, to, reason, today);

    public Guid EmployeeId { get; private init; }

    public LeaveKind Kind { get; private init; }

    public DateOnly From { get; private init; }

    public DateOnly To { get; private init; }

    public string Reason { get; private set; }

    public LeaveStatus Status { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public string? Outcome { get; private set; }

    /// <summary>
    /// Working days, counted here.
    /// </summary>
    /// <remarks>
    /// Weekends are left out; public holidays are not, because they vary and
    /// this system has no calendar of them. That is a known approximation
    /// rather than an oversight, and it errs towards charging somebody more
    /// leave than they owe — which somebody will notice and correct, unlike the
    /// other direction.
    /// </remarks>
    public int Days
    {
        get
        {
            var days = 0;

            for (var day = From; day <= To; day = day.AddDays(1))
            {
                if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                {
                    days++;
                }
            }

            return days;
        }
    }

    public bool IsLive => Status is LeaveStatus.Draft or LeaveStatus.AwaitingApproval
        or LeaveStatus.Approved;

    /// <summary>Does this overlap another request?</summary>
    public bool Overlaps(DateOnly from, DateOnly to) => From <= to && from <= To;

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
