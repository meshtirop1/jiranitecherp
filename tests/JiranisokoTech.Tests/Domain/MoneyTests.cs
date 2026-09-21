using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Tests.Domain;

/// <summary>
/// The arithmetic every invoice, budget and profitability figure rests on.
///
/// Worth testing hard and early: money bugs do not announce themselves. They
/// surface as a total that is out by a cent, months later, in front of a client.
/// </summary>
public class MoneyTests
{
    [Fact]
    public void Adds_and_subtracts_within_one_currency()
    {
        var a = Money.Of(1250, "KES");
        var b = Money.Of(750, "KES");

        Assert.Equal(Money.Of(2000, "KES"), a + b);
        Assert.Equal(Money.Of(500, "KES"), a - b);
    }

    /// <summary>
    /// The guard that matters. Adding shillings to dollars must fail loudly,
    /// not quietly produce a number that looks plausible.
    /// </summary>
    [Fact]
    public void Refuses_to_mix_currencies()
    {
        var shillings = Money.Of(1000, "KES");
        var dollars = Money.Of(1000, "USD");

        var thrown = Assert.Throws<InvalidOperationException>(() => shillings + dollars);

        Assert.Contains("KES", thrown.Message);
        Assert.Contains("USD", thrown.Message);
    }

    [Fact]
    public void Comparing_across_currencies_is_refused_too()
    {
        Assert.Throws<InvalidOperationException>(
            () => Money.Of(1, "KES") < Money.Of(1, "USD"));
    }

    [Theory]
    [InlineData("kes", "KES")]
    [InlineData(" usd ", "USD")]
    public void Normalises_the_currency_code(string given, string stored)
    {
        Assert.Equal(stored, Money.Of(1, given).Currency);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SHILLING")]
    [InlineData("K5S")]
    public void Rejects_anything_that_is_not_an_iso_code(string bad)
    {
        Assert.Throws<ArgumentException>(() => Money.Of(1, bad));
    }

    /// <summary>
    /// Tax and discounts. Away-from-zero, because that is what anybody checking
    /// the arithmetic on paper will have done.
    /// </summary>
    [Theory]
    [InlineData(1000, 0.16, 160)]   // 16% VAT on 10.00
    [InlineData(1005, 0.5, 503)]    // 5.025 rounds away from zero, not to even
    [InlineData(-1005, 0.5, -503)]
    public void Multiplies_by_a_rate_and_rounds_predictably(long minor, decimal rate, long expected)
    {
        Assert.Equal(expected, Money.Of(minor, "KES").Multiply(rate).MinorUnits);
    }

    /// <summary>
    /// Splitting a bill must not lose or invent a cent. Ten shillings across
    /// three lines is 3.34 + 3.33 + 3.33, and it adds back to exactly 10.00.
    /// </summary>
    [Fact]
    public void Allocating_never_loses_or_invents_a_minor_unit()
    {
        var total = Money.Of(1000, "KES");

        var parts = total.Allocate(3);

        Assert.Equal([334L, 333L, 333L], parts.Select(p => p.MinorUnits));
        Assert.Equal(total, parts.Aggregate(Money.Zero("KES"), (sum, part) => sum + part));
    }

    [Fact]
    public void Allocating_a_negative_amount_keeps_the_sign_and_still_balances()
    {
        var refund = Money.Of(-1000, "KES");

        var parts = refund.Allocate(3);

        Assert.Equal([-334L, -333L, -333L], parts.Select(p => p.MinorUnits));
        Assert.Equal(refund, parts.Aggregate(Money.Zero("KES"), (sum, part) => sum + part));
    }

    [Fact]
    public void Allocating_into_fewer_than_one_part_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Money.Of(100, "KES").Allocate(0));
    }

    [Fact]
    public void Reads_as_people_write_it()
    {
        Assert.Equal("KES 12.34", Money.Of(1234, "KES").ToString());
    }
}
