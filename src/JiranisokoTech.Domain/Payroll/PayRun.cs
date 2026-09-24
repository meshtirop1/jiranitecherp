using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Payroll;

/*
 * Inside the namespace rather than above it, and that is not a style choice. Money is both a
 * type in Domain.Common and a namespace at Domain.Money, and from inside Domain.Payroll the
 * compiler finds the sibling namespace first — an alias declared in the global scope loses to
 * it. Declared here it is in the namespace's own scope and wins. Invoice.cs solves the same
 * collision by writing Common.Money at every use.
 */
using Money = JiranisokoTech.Domain.Common.Money;

/// <summary>Where a pay run has got to.</summary>
public enum PayRunStatus
{
    /// <summary>Being put together. Nothing has been paid and nothing is fixed.</summary>
    Draft = 1,

    /// <summary>Agreed. The figures stop moving from here.</summary>
    Approved = 2,

    /// <summary>The money has left the account.</summary>
    Paid = 3,

    /// <summary>
    /// Drafted and then abandoned, with a reason.
    /// </summary>
    /// <remarks>
    /// Kept rather than deleted, for the reason a void invoice is kept: somebody asking next
    /// year why March was run twice needs the first one to still be there saying why it was
    /// not used.
    /// </remarks>
    Abandoned = 4,
}

/// <summary>
/// One period's pay for the whole firm.
/// </summary>
/// <remarks>
/// Section 22, and the last gap in section 94's finance chain. The
/// income-and-expenditure report calls its bottom line "a difference" and never "profit",
/// and says why in its own remarks: there is no payroll here, so the figure is wrong by the
/// largest cost the firm has.
///
/// <b>Approval is what freezes it, and that is the whole shape of this aggregate.</b> A draft
/// is a calculation somebody is still checking, and it can be thrown away and rebuilt from the
/// employment terms as often as anybody likes. Once approved the figures stop moving, because
/// an approved run is what somebody files a return against and what the accounts are written
/// from — the same reason an invoice freezes when it is sent.
///
/// Paying is separate from approving, and separate for the reason expense claims separate them
/// (see <c>Permissions.ExpensesPay</c>): approval says the figures are right, and payment says
/// money has left the account. One person can be allowed to do the first and not the second.
/// </remarks>
public sealed class PayRun : Entity, IAuditable
{
    private readonly List<Payslip> _payslips = [];

    private PayRun()
    {
        Currency = string.Empty;
    }

    private PayRun(DateOnly periodStart, DateOnly periodEnd, string currency, DateTimeOffset at)
    {
        if (periodEnd < periodStart)
        {
            throw new ArgumentException(
                "A pay period ends after it starts.", nameof(periodEnd));
        }

        PeriodStart = periodStart;
        PeriodEnd = periodEnd;
        Currency = Money.Of(0, currency).Currency;
        Status = PayRunStatus.Draft;
        DraftedAt = at;
    }

    public static PayRun Draft(
        DateOnly periodStart, DateOnly periodEnd, string currency, DateTimeOffset at) =>
        new(periodStart, periodEnd, currency, at);

    public DateOnly PeriodStart { get; private init; }

    public DateOnly PeriodEnd { get; private init; }

    /// <summary>
    /// The one currency this run is in.
    /// </summary>
    /// <remarks>
    /// One, not several, because <c>Money</c> refuses to add two currencies together and a run
    /// whose total cannot be computed is not a run. Somebody paid in another currency is
    /// excluded from this one and told so by name, rather than being silently converted at a
    /// rate nobody chose — see <c>ExchangeRate</c>, which says a conversion is a report and
    /// never a record.
    /// </remarks>
    public string Currency { get; private init; }

    public PayRunStatus Status { get; private set; }

    public DateTimeOffset DraftedAt { get; private init; }

    public DateTimeOffset? ApprovedAt { get; private set; }

    public Guid? ApprovedById { get; private set; }

    public DateTimeOffset? PaidAt { get; private set; }

    /// <summary>Why it was abandoned, when it was.</summary>
    public string? Outcome { get; private set; }

    /// <summary>
    /// The payslips in this run.
    /// </summary>
    /// <remarks>
    /// A copy, not the backing list. Handing EF the real list makes it treat added rows as
    /// updates, and then nothing is saved and nothing complains — a trap this codebase has
    /// already paid for once.
    /// </remarks>
    public IReadOnlyList<Payslip> Payslips => _payslips.ToList();

    public bool IsOpen => Status == PayRunStatus.Draft;

    /// <summary>What the firm owes in total before anything is deducted.</summary>
    public Money Gross => Total(slip => slip.Gross);

    /// <summary>What will actually be paid out.</summary>
    public Money Net => Total(slip => slip.Net);

    /// <summary>
    /// What this run costs the firm.
    /// </summary>
    /// <remarks>
    /// Gross rather than net, and the difference is the whole of why this property exists
    /// rather than being left to the reader. What leaves the firm is the gross: the deductions
    /// go to KRA and the funds rather than staying behind, so a cost figure built from net
    /// would understate the firm's largest cost by roughly a third and would do it quietly.
    /// </remarks>
    public Money Cost => Gross;

    public void Add(Payslip payslip)
    {
        Draftable();

        if (!string.Equals(payslip.Currency, Currency, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"This run is in {Currency} and that payslip is in {payslip.Currency}. A run "
                + "holds one currency, because a total across two of them cannot be computed.");
        }

        if (_payslips.Any(existing => existing.EmployeeId == payslip.EmployeeId))
        {
            throw new InvalidOperationException(
                "That person already has a payslip in this run.");
        }

        payslip.BelongsTo(Id);
        _payslips.Add(payslip);
    }

    /// <summary>
    /// Throw the calculation away, so it can be built again.
    /// </summary>
    /// <remarks>
    /// A draft exists to be rebuilt. Somebody corrects a salary, or a joiner is entered late,
    /// and the honest response is to draft the period again from the terms rather than to patch
    /// a figure — patching is how a payslip stops agreeing with the record it came from.
    /// </remarks>
    public void Clear()
    {
        Draftable();

        _payslips.Clear();
    }

    /// <summary>
    /// Agree the figures. Nothing moves afterwards.
    /// </summary>
    public void Approve(Guid approvedBy, DateTimeOffset at)
    {
        Draftable();

        if (_payslips.Count == 0)
        {
            throw new InvalidOperationException(
                "There is nobody in this run. Draft it against the staff list before approving "
                + "it.");
        }

        Status = PayRunStatus.Approved;
        ApprovedAt = at;
        ApprovedById = approvedBy;

        Raise(new PayRunApproved(Id, PeriodStart, PeriodEnd, Currency, Gross.MinorUnits, at));
    }

    /// <summary>The money has gone.</summary>
    public void Pay(DateTimeOffset at)
    {
        if (Status == PayRunStatus.Paid)
        {
            // Said rather than thrown, because pressing a button twice is not a fault and the
            // second press must not undo the first.
            return;
        }

        if (Status != PayRunStatus.Approved)
        {
            throw new InvalidOperationException(
                "A pay run is approved before it is paid. Approving says the figures are right; "
                + "paying says the money has left the account.");
        }

        Status = PayRunStatus.Paid;
        PaidAt = at;

        Raise(new PayRunPaid(Id, PeriodStart, PeriodEnd, Currency, Net.MinorUnits, at));
    }

    public void Abandon(string why)
    {
        if (Status is PayRunStatus.Approved or PayRunStatus.Paid)
        {
            throw new InvalidOperationException(
                "This run has been approved. An approved run is what a return is filed against, "
                + "so it stays.");
        }

        Status = PayRunStatus.Abandoned;
        Outcome = Required(why, nameof(why));
    }

    /// <summary>
    /// Nothing about a pay run's own record is a secret.
    /// </summary>
    /// <remarks>
    /// The period, who approved it and when are exactly what an audit trail is for. The pay
    /// itself is not here: every figure lives on <see cref="Payslip"/>, which is deliberately
    /// not auditable for that reason.
    ///
    /// The totals on this class are computed rather than stored, so they never reach the trail
    /// either — which matters more than it sounds, because a firm of three in one currency has
    /// a total that is one subtraction away from a salary.
    /// </remarks>
    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private Money Total(Func<Payslip, Money> of) =>
        _payslips.Count == 0
            ? Money.Zero(Currency)
            : _payslips.Aggregate(Money.Zero(Currency), (running, slip) => running + of(slip));

    private void Draftable()
    {
        if (Status != PayRunStatus.Draft)
        {
            throw new InvalidOperationException(
                Status == PayRunStatus.Abandoned
                    ? "This run was abandoned. Draft the period again."
                    : "This run has been approved, so its figures no longer move. Draft the "
                      + "period again if something was wrong.");
        }
    }

    private static string Required(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();
}

/// <summary>A pay run's figures were agreed.</summary>
/// <remarks>
/// Carries the total and never a person's share of it. An event is queued, logged and handled
/// by code nobody is looking at when it runs, which is the last place an individual salary
/// should end up.
/// </remarks>
public sealed record PayRunApproved(
    Guid PayRunId,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string Currency,
    long GrossMinorUnits,
    DateTimeOffset At) : DomainEvent;

/// <summary>A pay run was paid.</summary>
public sealed record PayRunPaid(
    Guid PayRunId,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string Currency,
    long NetMinorUnits,
    DateTimeOffset At) : DomainEvent;
