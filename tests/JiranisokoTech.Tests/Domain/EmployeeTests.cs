using JiranisokoTech.Domain.People;

namespace JiranisokoTech.Tests.Domain;

/// <summary>
/// A person the firm has a relationship with, and what may be done to that
/// relationship.
/// </summary>
public class EmployeeTests
{
    private static readonly DateOnly Monday = new(2026, 10, 5);

    private static Employee Hired(string name = "Vincent Bungei") =>
        Employee.Hire(name, Monday);

    private static Employee Started(string name = "Vincent Bungei")
    {
        var employee = Hired(name);
        employee.Start();
        employee.ClearEvents();

        return employee;
    }

    [Fact]
    public void Somebody_hired_has_a_record_before_they_have_started()
    {
        var employee = Employee.Hire("Charity Jepchirchir", Monday, jobTitle: " Delivery Lead ");

        Assert.Equal(EmploymentStatus.Invited, employee.Status);
        Assert.Equal(Monday, employee.StartsOn);
        Assert.Equal("Delivery Lead", employee.JobTitle);

        // Not assignable yet: a start date in the future is a normal thing to
        // record, and work must not reach somebody before their first day.
        Assert.False(employee.IsAssignable);
        Assert.True(employee.IsEmployed);

        var hired = Assert.Single(employee.Events.OfType<EmployeeHired>());
        Assert.Equal("Charity Jepchirchir", hired.FullName);
    }

    [Fact]
    public void A_name_is_required()
    {
        Assert.Throws<ArgumentException>(() => Employee.Hire("   ", Monday));
    }

    [Fact]
    public void Starting_makes_somebody_assignable()
    {
        var employee = Hired();

        employee.Start();

        Assert.Equal(EmploymentStatus.Active, employee.Status);
        Assert.True(employee.IsAssignable);
        Assert.Single(employee.Events.OfType<EmployeeStarted>());
    }

    [Fact]
    public void Somebody_cannot_start_twice()
    {
        var employee = Started();

        Assert.Throws<InvalidOperationException>(employee.Start);
    }

    /// <summary>
    /// Suspended is not the same as gone. They are still employed, their work
    /// stays theirs, and they may be back on Monday.
    /// </summary>
    [Fact]
    public void A_suspended_person_is_still_employed_but_takes_no_work()
    {
        var employee = Started();

        employee.Suspend("under investigation");

        Assert.Equal(EmploymentStatus.Suspended, employee.Status);
        Assert.True(employee.IsEmployed);
        Assert.False(employee.IsAssignable);
        Assert.Single(employee.Events.OfType<EmployeeSuspended>());
    }

    [Fact]
    public void Only_somebody_active_can_be_suspended()
    {
        var employee = Hired();

        Assert.Throws<InvalidOperationException>(() => employee.Suspend("no reason"));
    }

    [Fact]
    public void Leaving_records_the_day_and_keeps_the_person()
    {
        var employee = Started("Duncan");
        var friday = Monday.AddDays(300);

        employee.Leave(friday, "resigned");

        Assert.Equal(EmploymentStatus.Left, employee.Status);
        Assert.Equal(friday, employee.LeftOn);
        Assert.False(employee.IsEmployed);
        Assert.False(employee.IsAssignable);

        // The name is carried on the event, because whoever handles it has to
        // say who left without loading a row that may be gone by then.
        var left = Assert.Single(employee.Events.OfType<EmployeeLeft>());
        Assert.Equal("Duncan", left.FullName);
        Assert.Equal("resigned", left.Reason);
    }

    [Fact]
    public void Nobody_can_leave_before_they_started()
    {
        var employee = Started();

        Assert.Throws<ArgumentException>(() => employee.Leave(Monday.AddDays(-1), "changed mind"));
    }

    [Fact]
    public void Nobody_leaves_twice()
    {
        var employee = Started();
        employee.Leave(Monday, "resigned");

        Assert.Throws<InvalidOperationException>(() => employee.Leave(Monday, "again"));
    }

    /// <summary>
    /// Somebody who comes back comes back to the record they already have, so
    /// their history stays one person rather than two.
    /// </summary>
    [Fact]
    public void Somebody_who_left_can_be_taken_back_on_the_same_record()
    {
        var employee = Started();
        employee.Leave(Monday, "resigned");

        employee.Reinstate();

        Assert.Equal(EmploymentStatus.Active, employee.Status);
        Assert.Null(employee.LeftOn);
        Assert.Single(employee.Events.OfType<EmployeeReinstated>());
    }

    [Fact]
    public void Somebody_already_here_has_nothing_to_be_reinstated_from()
    {
        Assert.Throws<InvalidOperationException>(Started().Reinstate);
    }

    [Fact]
    public void Moving_department_says_where_from_and_where_to()
    {
        var employee = Started();
        var engineering = Guid.CreateVersion7();
        var delivery = Guid.CreateVersion7();

        employee.MoveTo(engineering);
        employee.MoveTo(delivery);

        var moves = employee.Events.OfType<EmployeeMoved>().ToList();

        Assert.Equal(2, moves.Count);
        Assert.Null(moves[0].FromDepartmentId);
        Assert.Equal(engineering, moves[1].FromDepartmentId);
        Assert.Equal(delivery, moves[1].ToDepartmentId);
    }

    /// <summary>
    /// Assigning the value it already holds is not an event. A trail where most
    /// entries mean nothing is a trail nobody reads.
    /// </summary>
    [Fact]
    public void Moving_somebody_where_they_already_are_does_nothing()
    {
        var employee = Started();
        var engineering = Guid.CreateVersion7();

        employee.MoveTo(engineering);
        employee.ClearEvents();
        employee.MoveTo(engineering);

        Assert.Empty(employee.Events);
    }

    [Fact]
    public void Nobody_reports_to_themselves()
    {
        var employee = Started();

        Assert.Throws<InvalidOperationException>(() => employee.ReportsTo(employee.Id));
    }

    [Fact]
    public void The_person_at_the_top_reports_to_nobody()
    {
        var employee = Started();
        employee.ReportsTo(Guid.CreateVersion7());
        employee.ClearEvents();

        employee.ReportsTo(null);

        Assert.Null(employee.ReportsToId);
        Assert.Single(employee.Events.OfType<ReportingLineChanged>());
    }

    [Fact]
    public void An_account_is_attached_once_and_not_quietly_swapped()
    {
        var employee = Started();
        var account = Guid.CreateVersion7();

        employee.LinkAccount(account);

        Assert.Equal(account, employee.AccountId);
        Assert.Single(employee.Events.OfType<EmployeeAccountLinked>());

        // Linking the same one again is not a change.
        employee.ClearEvents();
        employee.LinkAccount(account);
        Assert.Empty(employee.Events);

        // A different one is refused, so that swapping who somebody signs in as
        // has to be a deliberate act rather than a mistyped identifier.
        Assert.Throws<InvalidOperationException>(() => employee.LinkAccount(Guid.CreateVersion7()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    [InlineData(168)]
    public void Capacity_within_a_week_is_accepted(int hours)
    {
        var employee = Started();

        employee.SetWeeklyCapacity(hours);

        Assert.Equal(hours, employee.WeeklyCapacityHours);
    }

    /// <summary>
    /// A figure above the hours in a week is always a typo, and scheduling
    /// would quietly plan around it.
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(169)]
    [InlineData(400)]
    public void Capacity_outside_a_week_is_refused(int hours)
    {
        var employee = Started();

        Assert.Throws<ArgumentOutOfRangeException>(() => employee.SetWeeklyCapacity(hours));
    }

    [Fact]
    public void Somebody_hired_is_available_forty_hours_a_week_until_told_otherwise()
    {
        Assert.Equal(40, Hired().WeeklyCapacityHours);
    }
}
