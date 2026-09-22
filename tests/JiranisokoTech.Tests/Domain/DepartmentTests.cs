using JiranisokoTech.Domain.Common;
using JiranisokoTech.Domain.People;

namespace JiranisokoTech.Tests.Domain;

public class DepartmentTests
{
    [Fact]
    public void A_department_takes_its_handle_from_its_name()
    {
        var department = Department.Open("Field Operations");

        Assert.Equal("field-operations", department.Slug);
        Assert.Equal(Slug.From("Field Operations"), department.Handle);

        var opened = Assert.Single(department.Events.OfType<DepartmentOpened>());
        Assert.Equal("field-operations", opened.Slug);
    }

    [Fact]
    public void A_handle_can_be_given_rather_than_derived()
    {
        Assert.Equal("ops", Department.Open("Field Operations", slug: "ops").Slug);
    }

    /// <summary>
    /// The handle is what an address names, so it does not follow a rename.
    /// Re-deriving it would break every link anybody had saved, for tidiness
    /// nobody asked for.
    /// </summary>
    [Fact]
    public void Renaming_a_department_leaves_its_handle_alone()
    {
        var department = Department.Open("Field Operations");

        department.Rename("Delivery");

        Assert.Equal("Delivery", department.Name);
        Assert.Equal("field-operations", department.Slug);
    }

    [Fact]
    public void Appointing_a_head_says_who_it_was_and_who_it_is()
    {
        var department = Department.Open("Engineering");
        var charity = Guid.CreateVersion7();
        var tirop = Guid.CreateVersion7();

        department.AppointHead(charity);
        department.AppointHead(tirop);

        var changes = department.Events.OfType<DepartmentHeadChanged>().ToList();

        Assert.Equal(2, changes.Count);
        Assert.Null(changes[0].FromEmployeeId);
        Assert.Equal(charity, changes[1].FromEmployeeId);
        Assert.Equal(tirop, changes[1].ToEmployeeId);
    }

    /// <summary>
    /// The event matters more than the column: holding the post is what grants
    /// the department-head role, and losing it is what takes the role away.
    /// </summary>
    [Fact]
    public void Vacating_the_post_is_announced_like_any_other_change()
    {
        var department = Department.Open("Engineering");
        department.AppointHead(Guid.CreateVersion7());
        department.ClearEvents();

        department.AppointHead(null);

        Assert.Null(department.HeadEmployeeId);
        Assert.Single(department.Events.OfType<DepartmentHeadChanged>());
    }

    [Fact]
    public void Appointing_the_same_head_again_is_not_a_change()
    {
        var department = Department.Open("Engineering");
        var charity = Guid.CreateVersion7();

        department.AppointHead(charity);
        department.ClearEvents();
        department.AppointHead(charity);

        Assert.Empty(department.Events);
    }

    /// <summary>
    /// A stale role outliving the reason it was granted is exactly how somebody
    /// keeps an access nobody can account for.
    /// </summary>
    [Fact]
    public void Closing_a_department_vacates_its_post()
    {
        var department = Department.Open("Engineering");
        department.AppointHead(Guid.CreateVersion7());
        department.ClearEvents();

        department.Close();

        Assert.False(department.IsActive);
        Assert.Null(department.HeadEmployeeId);

        var vacated = Assert.Single(department.Events.OfType<DepartmentHeadChanged>());
        Assert.Null(vacated.ToEmployeeId);
        Assert.Single(department.Events.OfType<DepartmentClosed>());
    }

    [Fact]
    public void A_closed_department_cannot_be_given_a_head()
    {
        var department = Department.Open("Engineering");
        department.Close();

        Assert.Throws<InvalidOperationException>(
            () => department.AppointHead(Guid.CreateVersion7()));
    }

    [Fact]
    public void A_reopened_department_can_be_run_again()
    {
        var department = Department.Open("Engineering");
        department.Close();
        department.Reopen();
        department.ClearEvents();

        var purity = Guid.CreateVersion7();
        department.AppointHead(purity);

        Assert.True(department.IsActive);
        Assert.Equal(purity, department.HeadEmployeeId);
    }

    [Fact]
    public void Closing_something_already_closed_changes_nothing()
    {
        var department = Department.Open("Engineering");
        department.Close();
        department.ClearEvents();

        department.Close();

        Assert.Empty(department.Events);
    }

    [Fact]
    public void A_name_is_required()
    {
        Assert.Throws<ArgumentException>(() => Department.Open("  "));
    }
}
