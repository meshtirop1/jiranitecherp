using JiranisokoTech.Domain.Time;

namespace JiranisokoTech.Tests.Domain;

/// <summary>
/// The working-day rule, and the calendar it counts against.
/// </summary>
/// <remarks>
/// Held apart from the leave tests on purpose. This is the only definition of
/// "how many days off does this cost" in the system — the aggregate calls it and
/// the leave list reads the column it produced — so it is worth exercising
/// directly rather than only through a service and a database, where an
/// off-by-one in it would arrive as a puzzling figure on a screen.
/// </remarks>
public class HolidayTests
{
    /// <summary>Monday 21 December 2026 to Friday the 25th: an ordinary week.</summary>
    private static readonly DateOnly ChristmasWeek = new(2026, 12, 21);

    private static readonly DateOnly ChristmasDay = new(2026, 12, 25);

    /// <summary>Boxing Day 2026, which falls on a Saturday.</summary>
    private static readonly DateOnly BoxingDay = new(2026, 12, 26);

    [Fact]
    public void A_week_with_no_holidays_in_it_is_five_working_days()
    {
        var days = WorkingDays.Between(
            ChristmasWeek, ChristmasWeek.AddDays(4), new HashSet<DateOnly>());

        Assert.Equal(5, days);
    }

    [Fact]
    public void A_holiday_inside_the_range_takes_a_day_off_the_count()
    {
        var days = WorkingDays.Between(
            ChristmasWeek, ChristmasWeek.AddDays(4), new HashSet<DateOnly> { ChristmasDay });

        Assert.Equal(4, days);
    }

    /// <summary>
    /// A holiday on a weekend is not deducted twice.
    /// </summary>
    /// <remarks>
    /// The fault this rules out is the natural way to write the rule — weekdays
    /// in the range, less the holidays in the range — which subtracts a Saturday
    /// nobody was being charged for and hands out a day of leave that does not
    /// exist. Kenya puts a public holiday on a weekend most years, so this is not
    /// a corner: Boxing Day 2026 is a Saturday.
    /// </remarks>
    [Fact]
    public void A_holiday_falling_on_a_weekend_costs_nothing_and_gives_nothing_back()
    {
        // Monday the 21st to the following Monday the 28th: six working days,
        // with Boxing Day on the Saturday in the middle of it.
        var days = WorkingDays.Between(
            ChristmasWeek, ChristmasWeek.AddDays(7), new HashSet<DateOnly> { BoxingDay });

        Assert.Equal(6, days);
    }

    /// <summary>
    /// A holiday outside the range is not deducted either, which sounds obvious
    /// and is the other half of the same mistake.
    /// </summary>
    [Fact]
    public void A_holiday_outside_the_range_is_left_where_it_is()
    {
        var days = WorkingDays.Between(
            ChristmasWeek,
            ChristmasWeek.AddDays(2),
            new HashSet<DateOnly> { ChristmasDay, BoxingDay });

        Assert.Equal(3, days);
    }

    [Fact]
    public void A_single_day_is_counted_and_a_single_weekend_day_is_not()
    {
        var empty = new HashSet<DateOnly>();

        Assert.Equal(1, WorkingDays.Between(ChristmasDay, ChristmasDay, empty));
        Assert.Equal(0, WorkingDays.Between(BoxingDay, BoxingDay, empty));
    }

    [Fact]
    public void A_holiday_is_declared_on_a_date_with_a_name()
    {
        var holiday = Holiday.Declared(ChristmasDay, "  Christmas Day  ");

        Assert.Equal(ChristmasDay, holiday.On);
        Assert.Equal("Christmas Day", holiday.Name);
    }

    /// <summary>
    /// A date with nothing said about it cannot be reviewed against a gazette
    /// notice, which is the only thing this calendar has to support.
    /// </summary>
    [Fact]
    public void A_holiday_cannot_be_nameless()
    {
        Assert.Throws<ArgumentException>(() => Holiday.Declared(ChristmasDay, "   "));
    }

    [Fact]
    public void Renaming_a_holiday_leaves_its_date_alone()
    {
        var holiday = Holiday.Declared(new DateOnly(2027, 3, 20), "Idd-ul-Fitr");

        holiday.Rename("Idd-ul-Fitr (observed)");

        Assert.Equal("Idd-ul-Fitr (observed)", holiday.Name);
        Assert.Equal(new DateOnly(2027, 3, 20), holiday.On);
    }
}
