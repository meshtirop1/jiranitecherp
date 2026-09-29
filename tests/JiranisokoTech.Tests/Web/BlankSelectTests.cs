using JiranisokoTech.Application.People;
using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Tests.Identity;
using JiranisokoTech.Tests.Workflows;
using JiranisokoTech.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// Choosing "none" in a select is accepted as none.
/// </summary>
/// <remarks>
/// A browser posts the "none" option as an empty string, and Blazor's form mapping cannot read
/// "" as an identifier, so every form with such a select refused itself silently when "none"
/// was chosen — see <see cref="BlankChoices"/>. No earlier page test saw it, because the test
/// form filler reads inputs and not selects, so a select was never posted at all. These post
/// the empty value exactly as a browser does.
/// </remarks>
public class BlankSelectTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    /// <summary>
    /// The first person the firm adds has no department yet and nobody above them.
    /// </summary>
    [Fact]
    public async Task The_first_person_can_be_added_with_no_department_and_nobody_above_them()
    {
        var hr = await Browsing.SignedInAsync(factory, "blank-select-hr@jiranisokotech.co.ke", Roles.HumanResources);

        await Browsing.PressAsync(hr, "/people/new", "hire", new Dictionary<string, string>
        {
            ["Input.FullName"] = "Wanjiru Kamau",
            ["Input.JobTitle"] = "Managing Director",
            ["Input.StartsOn"] = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            ["Input.DepartmentId"] = "",
            ["Input.ReportsToId"] = "",
        });

        await factory.InScopeAsync(async services =>
        {
            var hired = await services.GetRequiredService<AppDbContext>().Employees.AsNoTracking()
                .SingleAsync(one => one.FullName == "Wanjiru Kamau");

            Assert.Null(hired.DepartmentId);
            Assert.Null(hired.ReportsToId);
        });
    }

    /// <summary>
    /// Work can be raised against no project and for nobody yet.
    /// </summary>
    [Fact]
    public async Task Work_can_be_raised_with_no_project_and_nobody_on_it()
    {
        const string email = "blank-select-lead@jiranisokotech.co.ke";
        var lead = await Browsing.SignedInAsync(factory, email, Roles.ProjectManager);

        await factory.InScopeAsync(async services =>
        {
            var people = services.GetRequiredService<PeopleService>();
            var me = await people.HireAsync("Otieno Blank", DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-1));
            await people.StartAsync(me.Id);

            var account = await services.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email);
            await people.LinkAccountAsync(me.Id, account!.Id);
        });

        var raised = await Browsing.PressAsync(lead, "/work/new", "raise", new Dictionary<string, string>
        {
            ["Input.Title"] = "Tidy the staging DNS records",
            ["Input.Kind"] = nameof(WorkItemKind.Task),
            ["Input.Priority"] = nameof(Priority.Normal),
            ["Input.ProjectId"] = "",
            ["Input.AssigneeId"] = "",
        });

        Browsing.Accepted(raised);

        await factory.InScopeAsync(async services =>
        {
            var item = await services.GetRequiredService<AppDbContext>().WorkItems.AsNoTracking()
                .SingleAsync(one => one.Title == "Tidy the staging DNS records");

            Assert.Null(item.ProjectId);
            Assert.Null(item.AssigneeId);
        });
    }

    /// <summary>
    /// Every select that offers "none" for an identifier is covered, found rather than listed.
    /// </summary>
    /// <remarks>
    /// These are the thirteen fields the fault was found on. The set is built by reflection, so
    /// a change to how it is built that loses some of them would otherwise pass unnoticed.
    /// </remarks>
    [Theory]
    [InlineData("Linking.EmployeeId")]
    [InlineData("Moving.EmployeeId")]
    [InlineData("Input.DepartmentId")]
    [InlineData("Input.ReportsToId")]
    [InlineData("Line.ManagerId")]
    [InlineData("Placement.DepartmentId")]
    [InlineData("Input.ProjectId")]
    [InlineData("Assignment.AssigneeId")]
    [InlineData("Leading.EmployeeId")]
    [InlineData("Serving.ClientId")]
    [InlineData("Input.AssigneeId")]
    public void Every_optional_identifier_in_a_form_is_known(string field) =>
        Assert.Contains(field, BlankChoices.Fields);

    /// <summary>
    /// A text field is not touched: an empty box bound to a string still arrives as empty.
    /// </summary>
    [Fact]
    public void Text_fields_are_left_alone() =>
        Assert.DoesNotContain("Input.FullName", BlankChoices.Fields);
}
