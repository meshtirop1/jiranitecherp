using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Time;
using JiranisokoTech.Infrastructure.Authorization;
using JiranisokoTech.Infrastructure.Time;
using JiranisokoTech.Tests.Infrastructure;

namespace JiranisokoTech.Tests.Reporting;

/// <summary>
/// The calendar, and the one thing it must never do.
/// </summary>
/// <remarks>
/// Section 33. The calendar reads twenty-odd sources that each have their own permission, so the
/// interesting tests are not that a date appears — they are that a date does <i>not</i>. A
/// calendar is the easiest screen in any system to leak from, because it is assembled from
/// everything at once and the reader never sees what was left out.
///
/// Two separate leaks, needing two separate answers: the <b>existence</b> of an entry, decided by
/// the source's permission and reach, and its <b>content</b>, decided by carrying almost none.
/// Both are tested here.
/// </remarks>
public class CalendarTests
{
    /// <summary>
    /// Somebody with no permissions sees the firm's dates and their own, and nobody else's.
    /// </summary>
    /// <remarks>
    /// The baseline. A holiday and an occasion are about the firm rather than about a person, so
    /// everybody sees them; another person's leave needs leave.view_all, which this reader does
    /// not hold.
    /// </remarks>
    [Fact]
    public async Task Without_permissions_the_calendar_shows_the_firm_and_yourself()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();

        var me = await Somebody(context, fixture, "Brian Kiptoo", null);
        var other = await Somebody(context, fixture, "Amina Wekesa", null);

        context.Holidays.Add(Holiday.Declared(fixture.Clock.Today.AddDays(3), "Madaraka Day"));
        context.Occasions.Add(Occasion.Planned(fixture.Clock.Today.AddDays(5), "All-hands"));
        await context.SaveChangesAsync();

        await Away(context, fixture, other, 7);
        await Away(context, fixture, me, 9);

        var calendar = new CalendarQueries(context, new Reaches(context));

        var entries = await calendar.BetweenAsync(
            fixture.Clock.Today,
            fixture.Clock.Today.AddDays(30),
            new HashSet<string>(),
            me);

        Assert.Contains(entries, one => one.Kind == DatedKind.Holiday);
        Assert.Contains(entries, one => one.Kind == DatedKind.Occasion);
        Assert.Contains(entries, one => one.Kind == DatedKind.MyLeave);

        // The whole point: somebody else's absence is not there at all.
        Assert.DoesNotContain(entries, one => one.Kind == DatedKind.Absence);
        Assert.DoesNotContain(entries, one => one.Title.Contains("Amina"));
    }

    /// <summary>
    /// Absence needs leave.view_all and a department the reader reaches.
    /// </summary>
    /// <remarks>
    /// Composed rather than copied: the department is already the unit of leave authority, so the
    /// department reach is the leave reach by this codebase's own definition. Holding the
    /// permission without reaching the department is the case that would leak if the two were
    /// written as one condition instead of two.
    /// </remarks>
    [Fact]
    public async Task Absence_needs_both_the_permission_and_the_department()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();

        var delivery = Department.Open("Delivery", "delivery");
        var office = Department.Open("Office", "office");

        context.Departments.AddRange(delivery, office);
        await context.SaveChangesAsync();

        var head = await Somebody(context, fixture, "Grace Wanjiru", delivery.Id);
        var mine = await Somebody(context, fixture, "Brian Kiptoo", delivery.Id);
        var theirs = await Somebody(context, fixture, "Faith Njeri", office.Id);

        delivery.AppointHead(head);
        await context.SaveChangesAsync();

        await Away(context, fixture, mine, 4);
        await Away(context, fixture, theirs, 8);

        var calendar = new CalendarQueries(context, new Reaches(context));

        var held = new HashSet<string>
        {
            Permissions.LeaveViewAll,
            Permissions.EmployeesView,
        };

        var entries = await calendar.BetweenAsync(
            fixture.Clock.Today, fixture.Clock.Today.AddDays(30), held, head);

        // Their own department, yes.
        Assert.Contains(entries, one => one.Title.Contains("Brian Kiptoo"));

        // The other department, no — the permission alone is not enough.
        Assert.DoesNotContain(entries, one => one.Title.Contains("Faith Njeri"));
    }

    /// <summary>
    /// An absence entry carries a name and nothing else.
    /// </summary>
    /// <remarks>
    /// The second leak, and the one a permission check cannot catch. Compassionate and sick are
    /// facts about a person's life, and a reason is worse — "away, bereavement" on a screen a
    /// department reads is a disclosure nobody consented to. Whoever may see more opens the page
    /// behind the link, which guards itself.
    /// </remarks>
    [Fact]
    public async Task An_absence_entry_says_who_and_nothing_more()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();

        var delivery = Department.Open("Delivery", "delivery");

        context.Departments.Add(delivery);
        await context.SaveChangesAsync();

        var head = await Somebody(context, fixture, "Grace Wanjiru", delivery.Id);
        var person = await Somebody(context, fixture, "Brian Kiptoo", delivery.Id);

        delivery.AppointHead(head);
        await context.SaveChangesAsync();

        await Away(context, fixture, person, 4, LeaveKind.Compassionate, "a death in the family");

        var calendar = new CalendarQueries(context, new Reaches(context));

        var entries = await calendar.BetweenAsync(
            fixture.Clock.Today,
            fixture.Clock.Today.AddDays(30),
            new HashSet<string> { Permissions.LeaveViewAll, Permissions.EmployeesView },
            head);

        var absence = Assert.Single(entries, one => one.Kind == DatedKind.Absence);

        Assert.Equal("Brian Kiptoo — away", absence.Title);

        var everything = absence.Title + absence.Detail + absence.Href;

        Assert.DoesNotContain("ompassionate", everything);
        Assert.DoesNotContain("death", everything);
    }

    /// <summary>
    /// A window typed backwards returns nothing rather than throwing.
    /// </summary>
    /// <remarks>
    /// The dates come from a query string, so they are whatever somebody pasted into the address
    /// bar. An error screen for a nonsensical window is an error screen anybody can produce.
    /// </remarks>
    [Fact]
    public async Task A_backwards_window_is_empty_rather_than_an_error()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();

        context.Holidays.Add(Holiday.Declared(fixture.Clock.Today, "Today"));
        await context.SaveChangesAsync();

        var calendar = new CalendarQueries(context, new Reaches(context));

        var entries = await calendar.BetweenAsync(
            fixture.Clock.Today.AddDays(10),
            fixture.Clock.Today,
            new HashSet<string>(),
            null);

        Assert.Empty(entries);
    }

    /// <summary>
    /// An occasion cannot end before it starts.
    /// </summary>
    /// <remarks>
    /// The typo a pair of date fields invites, and worth refusing for the reason an agreement
    /// refuses it: a range that ends before it begins would sit on the calendar for ever with
    /// nobody able to say what it was.
    /// </remarks>
    [Fact]
    public void An_occasion_cannot_end_before_it_starts()
    {
        var today = new DateOnly(2026, 9, 24);

        Assert.Throws<ArgumentException>(
            () => Occasion.Planned(today, "Office closed", today.AddDays(-1)));

        var occasion = Occasion.Planned(today, "Office closed", today.AddDays(3));

        Assert.Throws<ArgumentException>(() => occasion.Runs(today, today.AddDays(-1)));
    }

    /// <summary>
    /// An occasion running over several days appears once, not once a day.
    /// </summary>
    /// <remarks>
    /// Repeating it is what turns a week-long office closure into five rows that push everything
    /// else off the screen.
    /// </remarks>
    [Fact]
    public async Task A_multi_day_occasion_appears_once_with_its_range_in_words()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();

        context.Occasions.Add(Occasion.Planned(
            fixture.Clock.Today.AddDays(2), "Office closed", fixture.Clock.Today.AddDays(6)));

        await context.SaveChangesAsync();

        var calendar = new CalendarQueries(context, new Reaches(context));

        var entries = await calendar.BetweenAsync(
            fixture.Clock.Today, fixture.Clock.Today.AddDays(30), new HashSet<string>(), null);

        var occasion = Assert.Single(entries, one => one.Kind == DatedKind.Occasion);

        Assert.Equal(fixture.Clock.Today.AddDays(2), occasion.On);
        Assert.Contains("until", occasion.Detail);
    }

    private static async Task<Guid> Somebody(
        TestDbContext context, DatabaseFixture fixture, string name, Guid? departmentId)
    {
        var employee = Employee.Hire(name, fixture.Clock.Today, departmentId, "Engineer");

        employee.Start();
        context.Employees.Add(employee);
        await context.SaveChangesAsync();

        return employee.Id;
    }

    private static async Task Away(
        TestDbContext context,
        DatabaseFixture fixture,
        Guid employeeId,
        int inDays,
        LeaveKind kind = LeaveKind.Annual,
        string? reason = null)
    {
        var from = fixture.Clock.Today.AddDays(inDays);

        var request = LeaveRequest.For(
            employeeId,
            kind,
            from,
            from.AddDays(1),
            reason ?? "away",
            fixture.Clock.Today,
            new HashSet<DateOnly>());

        context.Leave.Add(request);
        await context.SaveChangesAsync();
    }
}
