using JiranisokoTech.Domain.Common;
using JiranisokoTech.Domain.Money;
using Project = JiranisokoTech.Domain.Work.Project;

namespace JiranisokoTech.Tests.Business;

/// <summary>
/// Rates, and the arithmetic they make possible.
/// </summary>
/// <remarks>
/// Money already refuses to add shillings to dollars, which is the half of section 57
/// that prevents silent errors. This is the other half, and it is the dangerous half:
/// every conversion produces a number that looks exactly like money whether or not it
/// is right.
/// </remarks>
public class MoneyAcrossCurrenciesTests
{
    private static readonly DateOnly September = new(2026, 9, 1);

    [Fact]
    public void A_rate_converts_in_the_direction_it_says()
    {
        var rate = ExchangeRate.Record("USD", "KES", 129.45m, September);

        // One dollar, in cents, at 129.45 — so 12,945 cents of shilling.
        var converted = rate.Apply(Money.Of(100, "USD"));

        Assert.Equal("KES", converted.Currency);
        Assert.Equal(12_945, converted.MinorUnits);
    }

    /// <summary>
    /// An amount in the wrong currency is refused, not assumed.
    /// </summary>
    /// <remarks>
    /// A conversion that silently treated dollars as shillings because that rate happened
    /// to be loaded would produce a plausible number, and a plausible wrong number about
    /// money is worse than an exception.
    /// </remarks>
    [Fact]
    public void A_rate_refuses_an_amount_it_does_not_convert()
    {
        var rate = ExchangeRate.Record("USD", "KES", 129.45m, September);

        Assert.Throws<InvalidOperationException>(() => rate.Apply(Money.Of(100, "GBP")));
    }

    /// <summary>
    /// A rate of zero or less is refused.
    /// </summary>
    /// <remarks>
    /// A zero rate would make every amount it touched disappear, and a negative one would
    /// turn money owed into money owing. Both would pass through every other check in the
    /// system.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_rate_that_is_not_positive_is_refused(decimal rate) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ExchangeRate.Record("USD", "KES", rate, September));

    [Fact]
    public void A_currency_against_itself_is_refused() =>
        Assert.Throws<ArgumentException>(
            () => ExchangeRate.Record("KES", "KES", 1m, September));

    [Theory]
    [InlineData("US")]
    [InlineData("USDD")]
    [InlineData("US1")]
    [InlineData("")]
    public void Something_that_is_not_a_currency_code_is_refused(string code) =>
        Assert.Throws<ArgumentException>(
            () => ExchangeRate.Record(code, "KES", 1m, September));

    /// <summary>
    /// Rounding is away from zero, so a person checking by hand agrees.
    /// </summary>
    /// <remarks>
    /// Banker's rounding is defensible and is not what somebody with a calculator will do,
    /// and the whole value of a converted figure is that it can be checked.
    /// </remarks>
    [Fact]
    public void A_half_unit_rounds_away_from_zero()
    {
        // 1 cent at 1.5 is 1.5 minor units, which must become 2 rather than 2's neighbour.
        var rate = ExchangeRate.Record("USD", "KES", 1.5m, September);

        Assert.Equal(2, rate.Apply(Money.Of(1, "USD")).MinorUnits);
    }

    /// <summary>
    /// A rate is held to eight decimal places, which a weak currency needs.
    /// </summary>
    /// <remarks>
    /// One shilling in dollars is about 0.0077. Two decimal places would round that to a
    /// cent, and four to 0.0077 — eight leaves room for the pairs that need it.
    /// </remarks>
    [Fact]
    public void A_small_rate_survives_being_recorded()
    {
        var rate = ExchangeRate.Record("KES", "USD", 0.00772000m, September);

        // A million shillings in cents, converted, is about 77.20 dollars — 7,720 cents.
        Assert.Equal(7_720, rate.Apply(Money.Of(1_000_000, "KES")).MinorUnits);
    }

    /// <summary>
    /// A project budget cannot be negative.
    /// </summary>
    /// <remarks>
    /// A project cannot be agreed to earn the firm money by existing, and a negative
    /// budget would make every margin calculation report a profit.
    /// </remarks>
    [Fact]
    public void A_negative_budget_is_refused()
    {
        var project = Project.Begin("Delivery note printer", "printer");

        Assert.Throws<ArgumentOutOfRangeException>(
            () => project.Budgeted(Money.Of(-1, "KES")));
    }

    [Fact]
    public void A_budget_needs_both_a_number_and_a_currency()
    {
        var project = Project.Begin("Delivery note printer", "printer");

        Assert.Null(project.Budget);

        project.Budgeted(Money.Of(500_000_00, "KES"));

        Assert.Equal(Money.Of(500_000_00, "KES"), project.Budget);

        project.Budgeted(null);

        Assert.Null(project.Budget);
    }

    /// <summary>
    /// An expense cannot be moved to another project after it has been decided.
    /// </summary>
    /// <remarks>
    /// A cost moved after approval is a figure altered on somebody else's project without
    /// their approver having seen it.
    /// </remarks>
    [Fact]
    public void A_decided_claim_cannot_be_charged_elsewhere()
    {
        var claim = ExpenseClaim.For(
            Guid.CreateVersion7(),
            Money.Of(3_500_00, "KES"),
            ExpenseCategory.Travel,
            September,
            "Taxi to the client",
            September);

        // While it is still the claimant's, the project can be set.
        claim.ChargeTo(Guid.CreateVersion7());

        var at = new DateTimeOffset(September.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        claim.Submit(at);
        claim.Approved(at);

        Assert.Throws<InvalidOperationException>(() => claim.ChargeTo(Guid.CreateVersion7()));
    }
}
