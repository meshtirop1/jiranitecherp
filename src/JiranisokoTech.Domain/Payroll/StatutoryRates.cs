namespace JiranisokoTech.Domain.Payroll;

/*
 * See the note in PayRun.cs: the alias belongs inside the namespace, because Domain.Money is a
 * sibling namespace and beats a global-scope alias.
 */
using Money = JiranisokoTech.Domain.Common.Money;

using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

/// <summary>
/// One set of statutory rates, in force from a date.
/// </summary>
/// <remarks>
/// <b>Data this firm records, not law compiled into the application, and the reason is the same
/// one this codebase gives twice already.</b> Exchange rates are dated rows somebody enters —
/// "a conversion is a report, never a record" — and public holidays are managed as data with a
/// recount in the same transaction. PAYE bands, NSSF, SHIF and the housing levy are the same
/// kind of fact: they come from outside, they change by Act of Parliament, sometimes more than
/// once a year, and the firm finds out about it from its accountant rather than from a
/// deployment.
///
/// Compiled in, they would be wrong law on the day the Finance Act commences and would stay
/// wrong until somebody shipped a release. Recorded here, the person who signs the return is
/// the person who enters the bands, which is the right way round: they are the one who has read
/// the Act.
///
/// <b>What this is not.</b> It is not tax advice and the screen says so. This computes what the
/// rates it was given produce, shows the arithmetic on every payslip line, and leaves the
/// question of whether the rates are right where it belongs.
///
/// <b>Nothing is ever edited.</b> A set in force is a set some payslip was computed from, so a
/// change is a new set from a new date and the old one stays — which is what makes an approved
/// run from last March still explicable this March.
/// </remarks>
public sealed class StatutoryRates : Entity, IAuditable
{
    private readonly List<TaxBand> _bands = [];

    private StatutoryRates()
    {
        Currency = string.Empty;
    }

    private StatutoryRates(DateOnly from, string currency)
    {
        InForceFrom = from;
        Currency = Money.Of(0, currency).Currency;
    }

    public static StatutoryRates From(DateOnly from, string currency) => new(from, currency);

    /// <summary>
    /// The first day these apply to.
    /// </summary>
    /// <remarks>
    /// Matched against the pay period rather than against today, so drafting March again in
    /// June uses March's rates. A payroll that used today's rates to recompute an old month
    /// would produce a figure that disagreed with the one already filed.
    /// </remarks>
    public DateOnly InForceFrom { get; private init; }

    public string Currency { get; private init; }

    /// <summary>The PAYE bands, lowest first.</summary>
    public IReadOnlyList<TaxBand> Bands => _bands.OrderBy(band => band.FromMinorUnits).ToList();

    /// <summary>
    /// The monthly personal relief, taken off the tax rather than off the pay.
    /// </summary>
    /// <remarks>
    /// Off the tax, which is the whole point of it and the commonest way to get PAYE wrong by a
    /// couple of thousand shillings a month. It cannot take the tax below nothing.
    /// </remarks>
    public long PersonalReliefMinorUnits { get; private set; }

    /// <summary>What the employee pays into the pension fund, as a share of pensionable pay.</summary>
    public decimal PensionEmployeeRate { get; private set; }

    /// <summary>What the firm pays in on top.</summary>
    public decimal PensionEmployerRate { get; private set; }

    /// <summary>The ceiling the pension share is worked out up to, per period.</summary>
    public long PensionCeilingMinorUnits { get; private set; }

    /// <summary>The health contribution, as a share of gross.</summary>
    public decimal HealthRate { get; private set; }

    /// <summary>The least the health contribution can be, however small the pay.</summary>
    public long HealthFloorMinorUnits { get; private set; }

    /// <summary>The housing levy, as a share of gross, matched by the firm.</summary>
    public decimal HousingRate { get; private set; }

    /// <summary>
    /// Where these figures came from, so a payslip can be defended.
    /// </summary>
    /// <remarks>
    /// "Finance Act 2023, effective 1 July" rather than a blank. Somebody checking a payslip
    /// from two years ago needs to know which Act produced the band, and the only person who
    /// can say is the one who typed it in.
    /// </remarks>
    public string? Source { get; private set; }

    public void Band(Money from, Money? to, decimal rate)
    {
        if (rate is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rate), rate, "A tax rate is a share, between nothing and all of it.");
        }

        if (to is { } ceiling && ceiling <= from)
        {
            throw new ArgumentException("A band ends above where it starts.", nameof(to));
        }

        _bands.Add(new TaxBand(
            Same(from).MinorUnits, to is { } top ? Same(top).MinorUnits : null, rate));
    }

    public void Relief(Money monthly) => PersonalReliefMinorUnits = Same(monthly).MinorUnits;

    public void Pension(decimal employee, decimal employer, Money ceiling)
    {
        PensionEmployeeRate = Share(employee, nameof(employee));
        PensionEmployerRate = Share(employer, nameof(employer));
        PensionCeilingMinorUnits = Same(ceiling).MinorUnits;
    }

    public void Health(decimal rate, Money floor)
    {
        HealthRate = Share(rate, nameof(rate));
        HealthFloorMinorUnits = Same(floor).MinorUnits;
    }

    public void Housing(decimal rate) => HousingRate = Share(rate, nameof(rate));

    public void Cite(string source) => Source = source.Trim();

    /// <summary>
    /// The bands have no gap and no overlap, and the top one is open.
    /// </summary>
    /// <remarks>
    /// Checked when a set is recorded rather than when a payslip is computed, because a gap in
    /// the bands does not throw — it quietly taxes part of somebody's pay at nothing, and a
    /// payslip that is too small by a band is not obviously wrong to anybody looking at it.
    /// </remarks>
    public void RefuseIfTheBandsDoNotCoverEveryShilling()
    {
        if (_bands.Count == 0)
        {
            throw new InvalidOperationException(
                "There are no bands in this set, so every payslip computed from it would deduct "
                + "no tax at all.");
        }

        var ordered = Bands;

        if (ordered[0].FromMinorUnits != 0)
        {
            throw new InvalidOperationException(
                "The lowest band starts above nothing, so the first shillings of everybody's "
                + "pay would be taxed at no rate.");
        }

        for (var i = 0; i < ordered.Count - 1; i++)
        {
            if (ordered[i].ToMinorUnits != ordered[i + 1].FromMinorUnits)
            {
                throw new InvalidOperationException(
                    $"There is a gap or an overlap between the band ending at "
                    + $"{ordered[i].ToMinorUnits} and the one starting at "
                    + $"{ordered[i + 1].FromMinorUnits}.");
            }
        }

        if (ordered[^1].ToMinorUnits is not null)
        {
            throw new InvalidOperationException(
                "The highest band has a ceiling, so pay above it would be taxed at no rate. The "
                + "top band is open.");
        }
    }

    /// <summary>
    /// What these rates take off one gross figure, as the lines of a payslip.
    /// </summary>
    /// <remarks>
    /// <b>The order is the contestable part, and it is written here rather than hidden.</b> The
    /// rates are data because they change; the sequence they apply in is code because it is
    /// the shape of the law rather than a number in it. What this assumes:
    ///
    /// <list type="number">
    /// <item>The pension contribution comes off first, up to its ceiling, and reduces the pay
    /// that tax is worked out on.</item>
    /// <item>The housing levy comes off gross and also reduces taxable pay.</item>
    /// <item>The health contribution comes off gross and does NOT reduce taxable pay.</item>
    /// <item>PAYE is worked out on what is left, band by band, and the personal relief is then
    /// taken off the tax — not off the pay, which is the commonest way to be a couple of
    /// thousand shillings out every month.</item>
    /// </list>
    ///
    /// Every line carries its arithmetic in <c>Basis</c>, so an accountant can check the
    /// sequence against the Act without reading this file. If the sequence is wrong, the
    /// payslip says so plainly enough to be caught — which is the most an application can
    /// honestly offer about a law it does not get to read.
    /// </remarks>
    public IReadOnlyList<PayslipLine> Deductions(Money gross)
    {
        var pay = Same(gross);
        var lines = new List<PayslipLine>();

        var pensionable = pay.MinorUnits < PensionCeilingMinorUnits
            ? pay
            : Money.Of(PensionCeilingMinorUnits, Currency);

        var pension = pensionable.Multiply(PensionEmployeeRate);
        var housing = pay.Multiply(HousingRate);

        var health = pay.Multiply(HealthRate);

        if (health.MinorUnits < HealthFloorMinorUnits)
        {
            health = Money.Of(HealthFloorMinorUnits, Currency);
        }

        if (pension.MinorUnits > 0)
        {
            lines.Add(new PayslipLine(
                "Pension", pension.MinorUnits, DeductionKind.Statutory,
                $"{PensionEmployeeRate:P2} of {pensionable}"
                + (pay.MinorUnits > PensionCeilingMinorUnits
                    ? $", the ceiling on {pay}"
                    : string.Empty)));
        }

        if (housing.MinorUnits > 0)
        {
            lines.Add(new PayslipLine(
                "Housing levy", housing.MinorUnits, DeductionKind.Statutory,
                $"{HousingRate:P2} of {pay}"));
        }

        if (health.MinorUnits > 0)
        {
            lines.Add(new PayslipLine(
                "Health", health.MinorUnits, DeductionKind.Statutory,
                health.MinorUnits == HealthFloorMinorUnits && pay.Multiply(HealthRate) < health
                    ? $"the floor of {health}, above {HealthRate:P2} of {pay}"
                    : $"{HealthRate:P2} of {pay}"));
        }

        /*
         * Taxable pay: gross less the two contributions the law allows against it. The health
         * contribution is deliberately not among them — see the list above.
         */
        var taxable = pay - pension - housing;
        var tax = Tax(taxable, out var worked);

        var relief = Money.Of(PersonalReliefMinorUnits, Currency);
        var payable = tax > relief ? tax - relief : Money.Zero(Currency);

        if (payable.MinorUnits > 0 || tax.MinorUnits > 0)
        {
            lines.Add(new PayslipLine(
                "PAYE", payable.MinorUnits, DeductionKind.Statutory,
                $"{worked} on {taxable} taxable, less {relief} relief"));
        }

        if (PensionEmployerRate > 0 && pensionable.MinorUnits > 0)
        {
            lines.Add(new PayslipLine(
                "Pension, employer", pensionable.Multiply(PensionEmployerRate).MinorUnits,
                DeductionKind.Employer, $"{PensionEmployerRate:P2} of {pensionable}"));
        }

        if (HousingRate > 0 && housing.MinorUnits > 0)
        {
            lines.Add(new PayslipLine(
                "Housing levy, employer", housing.MinorUnits, DeductionKind.Employer,
                $"matched: {HousingRate:P2} of {pay}"));
        }

        return lines;
    }

    /// <summary>
    /// Tax on an amount, band by band.
    /// </summary>
    /// <remarks>
    /// Each band taxes only the part of the pay that falls inside it, which is what a
    /// progressive band means and what somebody implementing it from a table of percentages
    /// usually gets wrong the first time — taxing the whole amount at the top band's rate
    /// produces a figure several times too large at exactly the salaries where somebody
    /// notices.
    /// </remarks>
    private Money Tax(Money taxable, out string worked)
    {
        var tax = Money.Zero(Currency);
        var steps = new List<string>();

        foreach (var band in Bands)
        {
            if (taxable.MinorUnits <= band.FromMinorUnits)
            {
                break;
            }

            var ceiling = band.ToMinorUnits is { } top
                ? Math.Min(top, taxable.MinorUnits)
                : taxable.MinorUnits;

            var slice = Money.Of(ceiling - band.FromMinorUnits, Currency);

            if (slice.MinorUnits <= 0)
            {
                continue;
            }

            tax += slice.Multiply(band.Rate);
            steps.Add($"{band.Rate:P0} of {slice}");
        }

        worked = steps.Count == 0 ? "no band reached" : string.Join(" plus ", steps);

        return tax;
    }

    /// <summary>
    /// The rates themselves are not a secret, and the trail should carry them.
    /// </summary>
    /// <remarks>
    /// The opposite decision from <c>Payslip</c>, and for the opposite reason: a tax band is
    /// public law, it applies to everybody, and it reveals nothing about any one person. That
    /// somebody changed a band on a Tuesday is exactly the sort of thing worth being able to
    /// look up, because every payslip after it moved.
    /// </remarks>
    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private Money Same(Money amount) =>
        string.Equals(amount.Currency, Currency, StringComparison.Ordinal)
            ? amount
            : throw new InvalidOperationException(
                $"These rates are in {Currency} and that figure is in {amount.Currency}.");

    private static decimal Share(decimal rate, string parameter) =>
        rate is < 0 or > 1
            ? throw new ArgumentOutOfRangeException(
                parameter, rate, "A rate is a share, between nothing and all of it.")
            : rate;
}

/// <summary>
/// One PAYE band: pay from here to there is taxed at this rate.
/// </summary>
/// <param name="ToMinorUnits">Null on the top band, which has no ceiling.</param>
public sealed record TaxBand(long FromMinorUnits, long? ToMinorUnits, decimal Rate);
