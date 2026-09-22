using JiranisokoTech.Domain.People;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Tests.Infrastructure;

/// <summary>
/// That people and departments survive the round trip, and that the rules the
/// database is responsible for are actually on it.
/// </summary>
/// <remarks>
/// The domain tests prove the rules hold in memory. None of that means the
/// mapping kept them: an enum stored as the wrong type, a unique index that was
/// never created, a private setter EF cannot write — each one passes every unit
/// test and fails the first time somebody uses the feature.
/// </remarks>
public class PeoplePersistenceTests
{
    private static readonly DateOnly Monday = new(2026, 10, 5);

    [Fact]
    public async Task An_employee_comes_back_as_they_were_written()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        var account = Guid.CreateVersion7();
        Guid id;

        await using (var write = db.NewContext())
        {
            var department = Department.Open("Engineering");
            write.Departments.Add(department);

            var employee = Employee.Hire("Charity Jepchirchir", Monday, department.Id, "Delivery Lead");
            employee.Start();
            employee.LinkAccount(account);
            employee.SetWeeklyCapacity(32);

            write.Employees.Add(employee);
            await write.SaveChangesAsync();

            id = employee.Id;
        }

        await using var read = db.NewContext();
        var stored = await read.Employees.SingleAsync(employee => employee.Id == id);

        Assert.Equal("Charity Jepchirchir", stored.FullName);
        Assert.Equal("Delivery Lead", stored.JobTitle);
        Assert.Equal(EmploymentStatus.Active, stored.Status);
        Assert.Equal(Monday, stored.StartsOn);
        Assert.Equal(32, stored.WeeklyCapacityHours);
        Assert.Equal(account, stored.AccountId);
        Assert.True(stored.IsAssignable);
    }

    [Fact]
    public async Task A_department_keeps_its_handle_and_its_head()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        var head = Guid.CreateVersion7();
        Guid id;

        await using (var write = db.NewContext())
        {
            var department = Department.Open("Field Operations");
            department.AppointHead(head);

            write.Departments.Add(department);
            await write.SaveChangesAsync();

            id = department.Id;
        }

        await using var read = db.NewContext();
        var stored = await read.Departments.SingleAsync(department => department.Id == id);

        Assert.Equal("field-operations", stored.Slug);
        Assert.Equal("field-operations", stored.Handle.Value);
        Assert.Equal(head, stored.HeadEmployeeId);
        Assert.True(stored.IsActive);
    }

    /// <summary>
    /// The handle is what an address names, so two departments cannot share one.
    /// </summary>
    [Fact]
    public async Task Two_departments_cannot_share_a_handle()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        await using var write = db.NewContext();

        write.Departments.Add(Department.Open("Field Operations"));
        await write.SaveChangesAsync();

        write.Departments.Add(Department.Open("field operations"));

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => write.SaveChangesAsync());
    }

    /// <summary>
    /// Two employee rows sharing a login would make "who did this?"
    /// unanswerable.
    /// </summary>
    [Fact]
    public async Task Two_people_cannot_share_an_account()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        var account = Guid.CreateVersion7();

        await using var write = db.NewContext();

        var first = Employee.Hire("Tirop Meshack", Monday);
        first.LinkAccount(account);
        write.Employees.Add(first);
        await write.SaveChangesAsync();

        var second = Employee.Hire("Somebody Else", Monday);
        second.LinkAccount(account);
        write.Employees.Add(second);

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => write.SaveChangesAsync());
    }

    /// <summary>
    /// Having no account is the ordinary case for somebody who has not started,
    /// so the unique index must not make it exclusive.
    /// </summary>
    [Fact]
    public async Task Any_number_of_people_can_have_no_account()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        await using (var write = db.NewContext())
        {
            write.Employees.Add(Employee.Hire("Purity", Monday));
            write.Employees.Add(Employee.Hire("Duncan", Monday));
            write.Employees.Add(Employee.Hire("Precious", Monday));

            await write.SaveChangesAsync();
        }

        await using var read = db.NewContext();

        Assert.Equal(3, await read.Employees.CountAsync(person => person.AccountId == null));
    }

    /// <summary>
    /// Hiring somebody is a fact other modules act on — an account to open, a
    /// requisition to close — so it has to reach the outbox with the change.
    /// </summary>
    [Fact]
    public async Task Hiring_somebody_writes_the_fact_with_the_record()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        await using (var write = db.NewContext())
        {
            write.Employees.Add(Employee.Hire("Vincent Bungei", Monday));
            await write.SaveChangesAsync();
        }

        await using var read = db.NewContext();
        var message = await read.Outbox.SingleAsync();

        Assert.Equal(nameof(EmployeeHired), message.Type);
        Assert.True(message.IsPending);
    }

    /// <summary>
    /// Changes to a person are the ones somebody asks about afterwards, so they
    /// belong in the trail.
    /// </summary>
    [Fact]
    public async Task Moving_somebody_is_recorded_in_the_audit_trail()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        Guid id;

        await using (var write = db.NewContext())
        {
            var employee = Employee.Hire("Tirop Meshack", Monday);
            write.Employees.Add(employee);
            await write.SaveChangesAsync();

            id = employee.Id;
        }

        await using (var move = db.NewContext())
        {
            var employee = await move.Employees.SingleAsync(person => person.Id == id);
            employee.SetJobTitle("Head of Engineering");

            await move.SaveChangesAsync();
        }

        await using var read = db.NewContext();

        var entries = await read.AuditEntries
            .Where(entry => entry.SubjectId == id)
            .ToListAsync();

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, entry => entry.Action == "employee.modified");
    }

    /// <summary>
    /// Closing a department must not take the people in it with it. They are
    /// still employed; the department is what ended.
    /// </summary>
    [Fact]
    public async Task Deleting_a_department_leaves_its_people_where_they_are()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        Guid id;

        await using (var write = db.NewContext())
        {
            var department = Department.Open("Engineering");
            write.Departments.Add(department);

            var employee = Employee.Hire("Duncan", Monday, department.Id);
            write.Employees.Add(employee);
            await write.SaveChangesAsync();

            id = employee.Id;

            write.Departments.Remove(department);
            await write.SaveChangesAsync();
        }

        await using var read = db.NewContext();
        var stored = await read.Employees.SingleAsync(person => person.Id == id);

        Assert.Null(stored.DepartmentId);
        Assert.True(stored.IsEmployed);
    }
}
