using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Money;

/// <summary>
/// How often a standing cost falls due.
/// </summary>
/// <remarks>
/// The number is the month step, deliberately, so the arithmetic that moves a due date
/// forward is <c>AddMonths((int)every)</c> and there is no table mapping one to the other.
/// A separate lookup would be a second place for the two to disagree, and the day they did,
/// a quarterly cost would fall due monthly with nothing looking wrong.
/// </remarks>
public enum Recurrence
{
    Monthly = 1,
    Quarterly = 3,
    Annually = 12,
}

public enum RecurrenceStatus
{
    Active = 1,

    /// <summary>Still here, raising nothing.</summary>
    Paused = 2,
}

/// <summary>
/// A cost the firm pays on a timetable.
/// </summary>
/// <remarks>
/// Rent, a domain renewal, a software subscription: money that leaves every month whether or
/// not anybody remembers it. Before this they were expense claims somebody typed from memory,
/// which means the month somebody was on leave is a month the figure is wrong and nobody can
/// tell — a cost that is missing from a report looks exactly like a cost that was not
/// incurred.
///
/// <b>It raises charges; it does not pay anything.</b> A charge is a record that this cost
/// fell due on a date, and marking one settled is somebody saying the money went out. Nothing
/// here touches a bank, and a system that claimed to would be claiming something it cannot
/// know.
///
/// <b>Charges are caught up, not back-filled silently.</b> The job that raises them works
/// forward from the last one raised, so a scheduler that was off for two months raises the two
/// it missed rather than one — and each carries the date it was actually due rather than the
/// day the job happened to run.
/// </remarks>
public sealed class RecurringExpense : Entity, IAuditable
{
    private readonly List<RecurringCharge> _charges = [];

    private RecurringExpense()
    {
        Description = string.Empty;
        Payee = string.Empty;
        Currency = string.Empty;
    }

    private RecurringExpense(
        Guid accountId,
        string description,
        string payee,
        Common.Money amount,
        Recurrence every,
        DateOnly startsOn,
        DateOnly? endsOn)
    {
        if (accountId == Guid.Empty)
        {
            throw new ArgumentException(
                "A standing cost has to be coded to an expense account, or it cannot appear "
                + "on the report it exists to appear on.",
                nameof(accountId));
        }

        if (amount.MinorUnits <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount), "A standing cost of nothing is not a cost.");
        }

        if (endsOn is { } last && last < startsOn)
        {
            throw new ArgumentException(
                "It cannot stop before it starts.", nameof(endsOn));
        }

        AccountId = accountId;
        Description = Require(description, nameof(description));
        Payee = Require(payee, nameof(payee));
        AmountMinorUnits = amount.MinorUnits;
        Currency = amount.Currency;
        Every = every;
        StartsOn = startsOn;
        EndsOn = endsOn;
        Status = RecurrenceStatus.Active;

        Raise(new RecurringExpenseScheduled(Id, accountId, Description, startsOn));
    }

    public static RecurringExpense Scheduled(
        Guid accountId,
        string description,
        string payee,
        Common.Money amount,
        Recurrence every,
        DateOnly startsOn,
        DateOnly? endsOn = null) =>
        new(accountId, description, payee, amount, every, startsOn, endsOn);

    public Guid AccountId { get; private set; }

    public string Description { get; private set; }

    /// <summary>Who it is paid to.</summary>
    public string Payee { get; private set; }

    public long AmountMinorUnits { get; private set; }

    public string Currency { get; private init; }

    public Common.Money Amount => Common.Money.Of(AmountMinorUnits, Currency);

    public Recurrence Every { get; private set; }

    public DateOnly StartsOn { get; private init; }

    /// <summary>When it stops, if it is known to.</summary>
    public DateOnly? EndsOn { get; private set; }

    public RecurrenceStatus Status { get; private set; }

    public bool IsActive => Status == RecurrenceStatus.Active;

    /// <remarks>Returns a copy — see the note on Invoice.Lines for why.</remarks>
    public IReadOnlyList<RecurringCharge> Charges => _charges.ToList();

    /// <summary>
    /// The next date a charge is due, or nothing if it has run its course.
    /// </summary>
    /// <remarks>
    /// Counted from the last charge raised rather than from today, which is what makes the
    /// catch-up work: a scheduler that has been off for two months finds the oldest missing
    /// date rather than skipping to the current one and losing the gap for ever.
    /// </remarks>
    public DateOnly? NextDueOn()
    {
        if (!IsActive)
        {
            return null;
        }

        var next = _charges.Count == 0
            ? StartsOn
            : _charges.Max(charge => charge.DueOn).AddMonths((int)Every);

        return EndsOn is { } last && next > last ? null : next;
    }

    /// <summary>
    /// Raise the charge that is due on this date.
    /// </summary>
    /// <remarks>
    /// Silent about a date already raised, which is what makes the job safe to run twice —
    /// the same rule the reminder ledger enforces for mail, for the same reason. Without it a
    /// scheduler restarted twice in a morning would post three months' rent.
    /// </remarks>
    public RecurringCharge? RaiseFor(DateOnly dueOn, DateTimeOffset at)
    {
        if (!IsActive || _charges.Any(charge => charge.DueOn == dueOn))
        {
            return null;
        }

        if (dueOn < StartsOn || (EndsOn is { } last && dueOn > last))
        {
            return null;
        }

        var charge = RecurringCharge.Of(dueOn, AmountMinorUnits, Currency, at);

        _charges.Add(charge);

        Raise(new RecurringChargeRaised(
            Id, charge.Id, AccountId, Description, AmountMinorUnits, Currency, dueOn));

        return charge;
    }

    /// <summary>Somebody says the money went out.</summary>
    public void Settled(Guid chargeId, DateOnly on)
    {
        _charges.FirstOrDefault(charge => charge.Id == chargeId)?.Settle(on);
    }

    public void Pause() => Status = RecurrenceStatus.Paused;

    public void Resume() => Status = RecurrenceStatus.Active;

    /// <summary>
    /// Change what it costs, or who it is paid to.
    /// </summary>
    /// <remarks>
    /// Charges already raised keep the figure they were raised at, because that is what fell
    /// due then — a rent rise in July must not rewrite what was owed in June, and a report
    /// that changed retrospectively would be one nobody could reconcile with a bank
    /// statement.
    /// </remarks>
    public void Costs(Common.Money amount, string payee)
    {
        if (amount.MinorUnits <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount), "A standing cost of nothing is not a cost.");
        }

        if (amount.Currency != Currency)
        {
            throw new InvalidOperationException(
                $"This cost is in {Currency}. Changing the currency would rewrite what the "
                + "charges already raised were worth, so raise a new one instead.");
        }

        AmountMinorUnits = amount.MinorUnits;
        Payee = Require(payee, nameof(payee));
    }

    public void Until(DateOnly? endsOn)
    {
        if (endsOn is { } last && last < StartsOn)
        {
            throw new ArgumentException("It cannot stop before it starts.", nameof(endsOn));
        }

        EndsOn = endsOn;
    }

    public void CodeTo(Guid accountId)
    {
        if (accountId == Guid.Empty)
        {
            throw new ArgumentException(
                "A standing cost has to be coded to an account.", nameof(accountId));
        }

        AccountId = accountId;
    }

    public void Retitle(string description) =>
        Description = Require(description, nameof(description));

    public void RepeatsEvery(Recurrence every) => Every = every;

    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private static string Require(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();
}

/// <summary>
/// One occurrence of a standing cost.
/// </summary>
/// <remarks>
/// Carries its own amount rather than reading the parent's, because the parent's can change
/// and this one is what fell due on that date. A rent rise in July must not rewrite June.
/// </remarks>
public sealed class RecurringCharge
{
    private RecurringCharge()
    {
        Currency = string.Empty;
    }

    internal static RecurringCharge Of(
        DateOnly dueOn, long minorUnits, string currency, DateTimeOffset at) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            DueOn = dueOn,
            AmountMinorUnits = minorUnits,
            Currency = currency,
            RaisedAt = at,
        };

    public Guid Id { get; private init; }

    public DateOnly DueOn { get; private init; }

    public long AmountMinorUnits { get; private init; }

    public string Currency { get; private init; }

    public Common.Money Amount => Common.Money.Of(AmountMinorUnits, Currency);

    public DateTimeOffset RaisedAt { get; private init; }

    /// <summary>When somebody said the money went out.</summary>
    public DateOnly? SettledOn { get; private set; }

    public bool IsSettled => SettledOn is not null;

    internal void Settle(DateOnly on) => SettledOn ??= on;
}

public sealed record RecurringExpenseScheduled(
    Guid ExpenseId,
    Guid AccountId,
    string Description,
    DateOnly StartsOn) : DomainEvent;

/// <summary>A standing cost fell due.</summary>
/// <remarks>
/// Carries the amount, because this is the event a cost report would be built from if
/// anything ever consumed it. Nothing does yet, and the report sums the charges directly.
/// </remarks>
public sealed record RecurringChargeRaised(
    Guid ExpenseId,
    Guid ChargeId,
    Guid AccountId,
    string Description,
    long AmountMinorUnits,
    string Currency,
    DateOnly DueOn) : DomainEvent;
