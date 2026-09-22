using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Money;

public enum ExpenseCategory
{
    Travel = 1,
    Accommodation = 2,
    Meals = 3,
    Equipment = 4,
    Software = 5,
    Training = 6,
    Other = 7,
}

public enum ClaimStatus
{
    Draft = 1,
    AwaitingApproval = 2,
    Approved = 3,
    Refused = 4,

    /// <summary>Approved and the money has gone out.</summary>
    Paid = 5,

    Withdrawn = 6,
}

/// <summary>
/// Money somebody spent that the firm owes them back.
/// </summary>
/// <remarks>
/// Goes up the same approval chain as leave and requisitions. The one rule
/// worth stating twice is that approval and payment are separate states:
/// agreeing a claim and actually paying it are done by different people at
/// different times, and a system that conflates them cannot answer "what do we
/// owe our staff right now?" — which is the question this table exists for.
/// </remarks>
public sealed class ExpenseClaim : Entity, IAuditable
{
    private ExpenseClaim()
    {
        Description = string.Empty;
        Currency = string.Empty;
    }

    private ExpenseClaim(
        Guid employeeId,
        Common.Money amount,
        ExpenseCategory category,
        DateOnly spentOn,
        string description,
        DateOnly today)
    {
        if (amount <= Common.Money.Zero(amount.Currency))
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount), amount, "A claim has to be for something.");
        }

        if (spentOn > today)
        {
            throw new ArgumentException(
                "That is in the future. Claim for it once you have spent it.", nameof(spentOn));
        }

        EmployeeId = employeeId;
        MinorUnits = amount.MinorUnits;
        Currency = amount.Currency;
        Category = category;
        SpentOn = spentOn;
        Description = Require(description, nameof(description));
        Status = ClaimStatus.Draft;

        Raise(new ExpenseClaimed(Id, employeeId, MinorUnits, Currency, category, spentOn));
    }

    public static ExpenseClaim For(
        Guid employeeId,
        Common.Money amount,
        ExpenseCategory category,
        DateOnly spentOn,
        string description,
        DateOnly today) =>
        new(employeeId, amount, category, spentOn, description, today);

    public Guid EmployeeId { get; private init; }

    /// <summary>
    /// Stored as its parts, and rebuilt as <see cref="Amount"/>.
    /// </summary>
    /// <remarks>
    /// Two plain columns rather than a mapped value object, so a report can sum
    /// them in SQL and a person can read them in a table viewer. The type is
    /// what the code works with; the columns are what the database holds.
    /// </remarks>
    public long MinorUnits { get; private set; }

    public string Currency { get; private init; }

    public Common.Money Amount => Common.Money.Of(MinorUnits, Currency);

    public ExpenseCategory Category { get; private set; }

    public DateOnly SpentOn { get; private init; }

    public string Description { get; private set; }

    /// <summary>What the receipt is stored as, if one came with it.</summary>
    public string? ReceiptStoredName { get; private set; }

    public string? ReceiptFileName { get; private set; }

    public ClaimStatus Status { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public DateTimeOffset? PaidAt { get; private set; }

    public string? Outcome { get; private set; }

    public bool HasReceipt => ReceiptStoredName is not null;

    /// <summary>Agreed, and the firm has not paid it yet.</summary>
    public bool IsOwed => Status == ClaimStatus.Approved;

    public void Attach(string originalName, string storedName)
    {
        if (Status is not (ClaimStatus.Draft or ClaimStatus.AwaitingApproval))
        {
            throw new InvalidOperationException(
                "This claim has been decided. A receipt added now would not be the one it was "
                + "approved against.");
        }

        ReceiptFileName = originalName.Trim();
        ReceiptStoredName = storedName;
    }

    public void Submit(DateTimeOffset at)
    {
        if (Status != ClaimStatus.Draft)
        {
            throw new InvalidOperationException(
                $"This is already {Status.ToString().ToLowerInvariant()}.");
        }

        Status = ClaimStatus.AwaitingApproval;

        Raise(new ExpenseSubmitted(Id, EmployeeId, MinorUnits, Currency, at));
    }

    public void Approved(DateTimeOffset at)
    {
        Awaiting();

        Status = ClaimStatus.Approved;
        DecidedAt = at;
        Outcome = null;

        Raise(new ExpenseApproved(Id, EmployeeId, MinorUnits, Currency, at));
    }

    public void Refused(string reason, DateTimeOffset at)
    {
        Awaiting();

        Status = ClaimStatus.Refused;
        DecidedAt = at;
        Outcome = Require(reason, nameof(reason));

        Raise(new ExpenseRefused(Id, EmployeeId, Outcome, at));
    }

    /// <summary>
    /// The money has gone out.
    /// </summary>
    /// <remarks>
    /// Only from approved. Paying a claim nobody agreed to is the thing this
    /// state machine exists to make impossible, and it is the one a finance
    /// system gets audited on.
    /// </remarks>
    public void Paid(DateTimeOffset at, string? reference)
    {
        if (Status != ClaimStatus.Approved)
        {
            throw new InvalidOperationException(
                $"This claim is {Status.ToString().ToLowerInvariant()}, so it cannot be paid.");
        }

        Status = ClaimStatus.Paid;
        PaidAt = at;
        Outcome = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();

        Raise(new ExpensePaid(Id, EmployeeId, MinorUnits, Currency, at));
    }

    public void Withdraw(DateTimeOffset at)
    {
        if (Status is not (ClaimStatus.Draft or ClaimStatus.AwaitingApproval))
        {
            throw new InvalidOperationException(
                $"This is {Status.ToString().ToLowerInvariant()} and can no longer be withdrawn.");
        }

        Status = ClaimStatus.Withdrawn;
        DecidedAt = at;
    }

    public void Amend(Common.Money amount, ExpenseCategory category, string description)
    {
        if (Status != ClaimStatus.Draft)
        {
            throw new InvalidOperationException(
                "This claim has been submitted. Withdraw it if the figures were wrong.");
        }

        if (amount <= Common.Money.Zero(amount.Currency))
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "A claim has to be for something.");
        }

        MinorUnits = amount.MinorUnits;
        Category = category;
        Description = Require(description, nameof(description));
    }

    /// <summary>Nothing withheld: a claim is a decision somebody answers for.</summary>
    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private void Awaiting()
    {
        if (Status != ClaimStatus.AwaitingApproval)
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

public sealed record ExpenseClaimed(
    Guid ClaimId,
    Guid EmployeeId,
    long MinorUnits,
    string Currency,
    ExpenseCategory Category,
    DateOnly SpentOn) : DomainEvent;

public sealed record ExpenseSubmitted(
    Guid ClaimId,
    Guid EmployeeId,
    long MinorUnits,
    string Currency,
    DateTimeOffset At) : DomainEvent;

public sealed record ExpenseApproved(
    Guid ClaimId,
    Guid EmployeeId,
    long MinorUnits,
    string Currency,
    DateTimeOffset At) : DomainEvent;

public sealed record ExpenseRefused(
    Guid ClaimId, Guid EmployeeId, string Reason, DateTimeOffset At) : DomainEvent;

public sealed record ExpensePaid(
    Guid ClaimId,
    Guid EmployeeId,
    long MinorUnits,
    string Currency,
    DateTimeOffset At) : DomainEvent;
