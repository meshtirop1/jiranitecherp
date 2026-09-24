using JiranisokoTech.Domain.Payroll;
using Money = JiranisokoTech.Domain.Common.Money;

namespace JiranisokoTech.Tests.Payroll;

/// <summary>
/// What a set of statutory rates takes off a salary.
/// </summary>
/// <remarks>
/// The figures below are Kenya's as at the 2023 Finance Act, and they are <b>test data rather
/// than law in the application</b> — which is the whole point of <see cref="StatutoryRates"/>
/// being a recorded aggregate. They are here because arithmetic needs numbers to be checked
/// against, and using the real ones means the expected answers can be worked out by hand and
/// argued with.
///
/// The faults these exist to catch are the ones a progressive tax invites. Taxing the whole
/// salary at the top band's rate rather than each slice at its own gives a figure several times
/// too large, and at exactly the salaries where somebody notices. Taking the personal relief
/// off the pay instead of off the tax is a couple of thousand shillings a month, every month,
/// in the firm's favour. Neither throws.
/// </remarks>
public class StatutoryRatesTests
{
    private const string Currency = "KES";

    /// <summary>
    /// Each band taxes only the part of the pay that falls inside it.
    /// </summary>
    /// <remarks>
    /// Worked by hand, in minor units, so the expected figure is arguable rather than copied
    /// from whatever the code happened to produce:
    ///
    ///   pensionable 18,000 of 150,000, at 6%      =  1,080.00
    ///   housing levy 1.5% of 150,000              =  2,250.00
    ///   health 2.75% of 150,000                   =  4,125.00
    ///   taxable 150,000 less pension and housing  = 146,670.00
    ///   PAYE 10% of 24,000                        =  2,400.00
    ///      plus 25% of 8,333                      =  2,083.25
    ///      plus 30% of 114,337                    = 34,301.10
    ///      less personal relief                   = -2,400.00
    ///                                               ---------
    ///   PAYE payable                              = 36,384.35
    ///   net                                       = 106,160.65
    /// </remarks>
    [Fact]
    public void Tax_is_worked_out_band_by_band_and_relief_comes_off_the_tax()
    {
        var rates = Kenya2023();

        var lines = rates.Deductions(Shillings(150_000));

        Assert.Equal(108_000, Named(lines, "Pension").MinorUnits);
        Assert.Equal(225_000, Named(lines, "Housing levy").MinorUnits);
        Assert.Equal(412_500, Named(lines, "Health").MinorUnits);
        Assert.Equal(3_638_435, Named(lines, "PAYE").MinorUnits);
    }

    /// <summary>
    /// The relief cannot take the tax below nothing.
    /// </summary>
    /// <remarks>
    /// Somebody on twenty thousand a month owes less tax than the relief is worth. The
    /// arithmetic produces a negative, and a negative deduction is a payment — so without the
    /// floor this pays the lowest earners a bonus out of the PAYE line, every month, and calls
    /// it tax.
    /// </remarks>
    [Fact]
    public void Somebody_earning_less_than_the_relief_is_worth_pays_no_tax()
    {
        var rates = Kenya2023();

        var lines = rates.Deductions(Shillings(20_000));

        Assert.Equal(0, Named(lines, "PAYE").MinorUnits);
    }

    /// <summary>
    /// The pension is worked out up to its ceiling, not on the whole salary.
    /// </summary>
    [Fact]
    public void The_pension_share_stops_at_its_ceiling()
    {
        var rates = Kenya2023();

        var low = rates.Deductions(Shillings(10_000));
        var high = rates.Deductions(Shillings(900_000));

        // Under the ceiling: six per cent of the pay itself.
        Assert.Equal(60_000, Named(low, "Pension").MinorUnits);

        // Over it: six per cent of the ceiling, however large the salary.
        Assert.Equal(108_000, Named(high, "Pension").MinorUnits);
    }

    /// <summary>The health contribution has a floor.</summary>
    [Fact]
    public void The_health_contribution_never_falls_below_its_floor()
    {
        var rates = Kenya2023();

        // 2.75% of 5,000 is 137.50, which is under the floor of 300.
        var lines = rates.Deductions(Shillings(5_000));

        Assert.Equal(30_000, Named(lines, "Health").MinorUnits);
        Assert.Contains("floor", Named(lines, "Health").Basis);
    }

    /// <summary>
    /// What the firm pays on top is not taken off the person.
    /// </summary>
    /// <remarks>
    /// The fault this guards against understates take-home and overstates the firm's cost in
    /// one stroke, and it looks entirely plausible on the payslip: an employer contribution
    /// sitting in the deductions column is exactly the shape of a deduction.
    /// </remarks>
    [Fact]
    public void An_employer_contribution_is_a_cost_and_never_a_deduction()
    {
        var rates = Kenya2023();
        var gross = Shillings(150_000);

        var slip = Payslip.For(Guid.CreateVersion7(), gross, On(1), On(30));

        foreach (var line in rates.Deductions(gross))
        {
            slip.Take(line.Name, Money.Of(line.MinorUnits, Currency), line.Kind, line.Basis);
        }

        Assert.Equal(10_616_065, slip.Net.MinorUnits);

        // The employer's share is counted into what the person costs and into nothing else.
        Assert.True(slip.EmployerPays.MinorUnits > 0);
        Assert.Equal(slip.Gross + slip.EmployerPays, slip.Cost);
        Assert.Equal(slip.Gross - slip.Deducted, slip.Net);
    }

    /// <summary>
    /// A gap between two bands is refused when the rates are recorded.
    /// </summary>
    /// <remarks>
    /// Checked at the point of entry because of how it fails otherwise. A gap does not throw
    /// and does not look wrong: it quietly taxes a slice of everybody's pay at no rate, and the
    /// payslip that comes out is a plausible number that is simply too small.
    /// </remarks>
    [Fact]
    public void Bands_with_a_gap_in_them_are_refused()
    {
        var rates = StatutoryRates.From(new DateOnly(2026, 1, 1), Currency);

        rates.Band(Shillings(0), Shillings(24_000), 0.10m);

        // Starts at 30,000, leaving 24,000 to 30,000 taxed at nothing at all.
        rates.Band(Shillings(30_000), null, 0.30m);

        var refused = Assert.Throws<InvalidOperationException>(
            rates.RefuseIfTheBandsDoNotCoverEveryShilling);

        Assert.Contains("gap or an overlap", refused.Message);
    }

    /// <summary>The top band has no ceiling, or the best-paid pay nothing above it.</summary>
    [Fact]
    public void A_closed_top_band_is_refused()
    {
        var rates = StatutoryRates.From(new DateOnly(2026, 1, 1), Currency);

        rates.Band(Shillings(0), Shillings(24_000), 0.10m);
        rates.Band(Shillings(24_000), Shillings(500_000), 0.30m);

        var refused = Assert.Throws<InvalidOperationException>(
            rates.RefuseIfTheBandsDoNotCoverEveryShilling);

        Assert.Contains("top band is open", refused.Message);
    }

    /// <summary>Every line says how it was worked out.</summary>
    /// <remarks>
    /// A payslip has to be defensible years after everybody has forgotten the arithmetic.
    /// "PAYE 36,384.35" answers nothing on its own; the basis is what lets an accountant check
    /// the figure against the Act without reading any code.
    /// </remarks>
    [Fact]
    public void Every_line_carries_the_arithmetic_that_produced_it()
    {
        var lines = Kenya2023().Deductions(Shillings(150_000));

        Assert.All(lines, line => Assert.False(string.IsNullOrWhiteSpace(line.Basis)));

        Assert.Contains("30 %", Named(lines, "PAYE").Basis!.Replace("30%", "30 %"));
        Assert.Contains("relief", Named(lines, "PAYE").Basis);
    }

    /// <summary>
    /// Kenya as at the 2023 Finance Act, entered the way a person would enter it.
    /// </summary>
    private static StatutoryRates Kenya2023()
    {
        var rates = StatutoryRates.From(new DateOnly(2023, 7, 1), Currency);

        rates.Band(Shillings(0), Shillings(24_000), 0.10m);
        rates.Band(Shillings(24_000), Shillings(32_333), 0.25m);
        rates.Band(Shillings(32_333), Shillings(500_000), 0.30m);
        rates.Band(Shillings(500_000), Shillings(800_000), 0.325m);
        rates.Band(Shillings(800_000), null, 0.35m);

        rates.Relief(Shillings(2_400));
        rates.Pension(0.06m, 0.06m, Shillings(18_000));
        rates.Health(0.0275m, Shillings(300));
        rates.Housing(0.015m);
        rates.Cite("Finance Act 2023");

        rates.RefuseIfTheBandsDoNotCoverEveryShilling();

        return rates;
    }

    private static Money Shillings(long whole) => Money.Of(whole * 100, Currency);

    private static DateOnly On(int day) => new(2026, 9, day);

    private static PayslipLine Named(IReadOnlyList<PayslipLine> lines, string name) =>
        lines.SingleOrDefault(line => line.Name == name)
        ?? new PayslipLine(name, 0, DeductionKind.Statutory, "not present");
}
