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

/// <summary>What kind of thing was taken off, or added on.</summary>
public enum DeductionKind
{
    /// <summary>
    /// Required by law — PAYE, NSSF, SHIF, the housing levy.
    /// </summary>
    /// <remarks>
    /// Named apart from the rest because these are the ones somebody files a return about, and
    /// because they are the ones whose rates change by Act of Parliament rather than by
    /// agreement with the person being paid.
    /// </remarks>
    Statutory = 1,

    /// <summary>Agreed with the person: a salary advance being repaid, a pension top-up.</summary>
    Agreed = 2,

    /// <summary>
    /// Paid by the firm on top of the salary rather than taken out of it.
    /// </summary>
    /// <remarks>
    /// The employer's share of a statutory contribution is a real cost and is not a deduction:
    /// treating it as one would understate what somebody takes home and overstate what the firm
    /// pays, in one stroke. It is counted into the firm's cost and never into the net.
    /// </remarks>
    Employer = 3,
}

/// <summary>
/// One line on one payslip.
/// </summary>
/// <remarks>
/// The basis is kept beside the amount because a payslip has to be defensible years after
/// everybody has forgotten the arithmetic. "PAYE 14,200" answers nothing on its own; "PAYE
/// 14,200, 25% of the band above 32,333 less personal relief" can be checked by somebody
/// holding the Finance Act.
/// </remarks>
public sealed record PayslipLine(string Name, long MinorUnits, DeductionKind Kind, string? Basis);

/// <summary>
/// What one person is paid for one period, and what was taken off it.
/// </summary>
/// <remarks>
/// <b>Deliberately not IAuditable, and that is the most important decision in this file.</b>
///
/// Everything on a payslip is somebody's pay. The audit trail is append-only by design and is
/// never pruned, and this codebase has already gone to some trouble to keep salary out of it:
/// <c>Employee.AuditExcludes</c> names Details, Emergency and Terms so that changing a salary
/// records that it changed and refuses to record what to. Making a payslip auditable would
/// undo all of that at a stroke — it would write every figure for every person for every month
/// into exactly the table that decision was protecting.
///
/// So a payslip is its own record with its own permission, the way <c>SignInRecord</c> is. What
/// the trail carries about payroll is on <see cref="PayRun"/>: the period, who approved it and
/// when. Those are the acts. The figures are not.
///
/// <b>Frozen by its run rather than by itself.</b> A payslip has no state of its own; it is
/// draft while the run is draft and fixed once the run is approved. Giving it a second state
/// machine would let a slip and its run disagree, and a payslip that says approved inside a
/// draft run is a document nobody can act on.
/// </remarks>
public sealed class Payslip : Entity
{
    private readonly List<PayslipLine> _lines = [];

    private Payslip()
    {
        Currency = string.Empty;
    }

    private Payslip(Guid employeeId, Money gross, DateOnly from, DateOnly to)
    {
        if (gross.MinorUnits < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(gross), "A payslip is not a bill. Gross pay is not negative.");
        }

        EmployeeId = employeeId;
        Currency = gross.Currency;
        GrossMinorUnits = gross.MinorUnits;
        PaidFrom = from;
        PaidTo = to;
    }

    /// <summary>
    /// Start a payslip for somebody, for the part of the period they were employed.
    /// </summary>
    /// <param name="from">
    /// The first day this pays for, which is the later of the period's start and the day they
    /// started. A joiner on the twentieth is not paid for the nineteenth.
    /// </param>
    /// <param name="to">
    /// The last day this pays for — the earlier of the period's end and the day they left.
    /// </param>
    public static Payslip For(Guid employeeId, Money gross, DateOnly from, DateOnly to) =>
        new(employeeId, gross, from, to);

    public Guid PayRunId { get; private set; }

    public Guid EmployeeId { get; private init; }

    public string Currency { get; private init; }

    public long GrossMinorUnits { get; private init; }

    public DateOnly PaidFrom { get; private init; }

    public DateOnly PaidTo { get; private init; }

    /// <inheritdoc cref="PayRun.Payslips"/>
    public IReadOnlyList<PayslipLine> Lines => _lines.ToList();

    public Money Gross => Money.Of(GrossMinorUnits, Currency);

    /// <summary>What is taken out of the pay.</summary>
    public Money Deducted => Sum(line => line.Kind is DeductionKind.Statutory or DeductionKind.Agreed);

    /// <summary>What the firm pays on top, which the person never sees.</summary>
    public Money EmployerPays => Sum(line => line.Kind == DeductionKind.Employer);

    /// <summary>
    /// What reaches their account.
    /// </summary>
    /// <remarks>
    /// Computed, never stored, for the reason section 18 gives for having no general ledger: a
    /// stored total is a number that can disagree with the lines it was added up from, and on
    /// the day it does nobody can tell which of the two anybody acted on.
    /// </remarks>
    public Money Net => Gross - Deducted;

    /// <summary>What this person costs the firm for the period.</summary>
    public Money Cost => Gross + EmployerPays;

    /// <summary>
    /// Take something off, or add an employer's contribution on.
    /// </summary>
    /// <remarks>
    /// Refused once the run is approved, which the run enforces by refusing to hand out a slip
    /// to change — see <c>PayRun.Draftable</c>. A deduction cannot be negative: a correction to
    /// a previous period is a line on the next run with a name saying so, not a minus sign
    /// hidden among this month's deductions.
    /// </remarks>
    public void Take(string name, Money amount, DeductionKind kind, string? basis = null)
    {
        if (!string.Equals(amount.Currency, Currency, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"This payslip is in {Currency} and that line is in {amount.Currency}.");
        }

        if (amount.MinorUnits < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                "A deduction is not negative. Put a correction on the next run as its own line, "
                + "named so somebody reading the payslip can see what it is.");
        }

        _lines.Add(new PayslipLine(
            Required(name, nameof(name)), amount.MinorUnits, kind, basis?.Trim()));
    }

    /// <summary>
    /// Refuse a payslip that would pay somebody less than nothing.
    /// </summary>
    /// <remarks>
    /// Checked when the run is approved rather than as each line is added, because the order
    /// lines arrive in is an accident of the calculation and a slip that is briefly negative
    /// halfway through is not wrong yet.
    /// </remarks>
    public void RefuseIfItTakesMoreThanItPays()
    {
        if (Net.MinorUnits < 0)
        {
            throw new InvalidOperationException(
                $"The deductions on this payslip come to {Deducted}, which is more than the "
                + $"{Gross} it pays. Something is wrong with the figures rather than with the "
                + "person.");
        }
    }

    internal void BelongsTo(Guid payRunId) => PayRunId = payRunId;

    private Money Sum(Func<PayslipLine, bool> which) =>
        _lines.Where(which).Aggregate(
            Money.Zero(Currency),
            (running, line) => running + Money.Of(line.MinorUnits, Currency));

    private static string Required(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();
}
