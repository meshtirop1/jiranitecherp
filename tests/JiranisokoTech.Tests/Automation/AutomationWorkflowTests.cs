using JiranisokoTech.Application.Automation;
using JiranisokoTech.Application.People;
using JiranisokoTech.Domain.Automation;
using JiranisokoTech.Domain.Notices;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Tests.Identity;
using JiranisokoTech.Tests.Postgres;
using JiranisokoTech.Tests.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Automation;

/// <summary>
/// Sections 64 to 66 end to end: the shipped rules switched on from the rules page, the event
/// caused by the person who would cause it on the page they would use, and the outbox carrying
/// it to the work, the checklist and the notices.
/// </summary>
/// <remarks>
/// Every piece has tests of its own. This is the one that shows they join: that the templates
/// are in the database on a fresh start, that the switch on the page reaches the row, that a
/// client taken on through the client page reaches the matcher through the outbox and the run
/// reaches the board through the outbox again — and that all of it holds on PostgreSQL, where
/// the migration, the timestamps and the queries are the real ones.
/// </remarks>
public static class AutomationWorkflow
{
    public static async Task WalkAsync(ApplicationFactory factory)
    {
        var tag = Guid.CreateVersion7().ToString("N")[^6..];

        // --- people who will be told -------------------------------------------------------
        var seller = await Harness.PersonAsync(factory, $"Sales {tag}", Roles.Sales);
        var finance = await Harness.PersonAsync(factory, $"Finance {tag}", Roles.FinanceManager);
        var hrPerson = await Harness.PersonAsync(factory, $"HR {tag}", Roles.HumanResources);
        var it = await Harness.PersonAsync(factory, $"IT {tag}", Roles.DevOpsEngineer);
        var head = await Harness.PersonAsync(factory, $"Head {tag}");

        var department = await Harness.InScopeAsync(factory, async services =>
        {
            var people = services.GetRequiredService<PeopleService>();
            var opened = await people.OpenDepartmentAsync($"Engineering {tag}");

            await people.AppointHeadAsync(opened.Id, head);

            return opened.Id;
        });

        // --- the owner switches the three shipped rules on, from the rules page -------------
        var owner = await Browsing.SignedInAsync(factory, $"owner-{tag}@jiranisokotech.co.ke", Roles.Owner);

        var templates = await Harness.InScopeAsync(factory, services =>
            services.GetRequiredService<AppDbContext>().AutomationRules.AsNoTracking()
                .Where(one => one.TemplateKey != null)
                .ToDictionaryAsync(one => one.TemplateKey!, one => one.Id));

        foreach (var key in new[] { Templates.NewClient, Templates.NewEmployee, Templates.ProjectSetup })
        {
            Browsing.Accepted(await Browsing.PressAsync(owner, $"/automation/{templates[key]}", "switch-on"));
        }

        // --- a client is taken on, a project started and somebody hired, on their pages ------
        var sales = await Browsing.SignedInAsync(factory, $"sales-{tag}@jiranisokotech.co.ke", Roles.Sales);
        var clientName = $"Kilimo Co-op {tag}";

        Browsing.Accepted(await Browsing.PressAsync(sales, "/clients", "take-on", new Dictionary<string, string>
        {
            ["Input.Name"] = clientName,
        }));

        var manager = await Browsing.SignedInAsync(factory, $"pm-{tag}@jiranisokotech.co.ke", Roles.ProjectManager);
        var projectName = $"Farm app {tag}";

        Browsing.Accepted(await Browsing.PressAsync(manager, "/projects", "begin", new Dictionary<string, string>
        {
            ["Input.Name"] = projectName,
        }));

        var hr = await Browsing.SignedInAsync(factory, $"hr-{tag}@jiranisokotech.co.ke", Roles.HumanResources);
        var joinerName = $"Joiner {tag}";

        // Reporting to the head of the department, chosen from the select the way a browser
        // posts it. Leaving it on "Nobody" posts an empty value, which the page refuses — see
        // the report on this change.
        Browsing.Accepted(await Browsing.PressAsync(hr, "/people/new", "hire", new Dictionary<string, string>
        {
            ["Input.FullName"] = joinerName,
            ["Input.StartsOn"] = "2026-11-02",
            ["Input.DepartmentId"] = department.ToString(),
            ["Input.ReportsToId"] = head.ToString(),
        }));

        // --- the outbox: the three events, then the three runs -----------------------------
        await Harness.DrainAsync(factory);

        foreach (var key in new[] { Templates.NewClient, Templates.NewEmployee, Templates.ProjectSetup })
        {
            var run = Assert.Single(await Harness.RunsAsync(factory, templates[key]));

            Assert.True(run.Status == AutomationRunStatus.Done,
                $"{key}: {run.Status}. {run.Error} "
                + string.Join("; ", run.Steps.Select(step => $"{step.Describing} — {step.Outcome}")));
        }

        await Harness.InScopeAsync(factory, async services =>
        {
            var database = services.GetRequiredService<AppDbContext>();
            var titles = await database.WorkItems.AsNoTracking().Select(one => one.Title).ToListAsync();

            // Section 66: the client's record needs a person for three things.
            Assert.Contains($"Confirm billing details for {clientName}", titles);
            Assert.Contains($"Attach the signed agreement for {clientName} to their record", titles);

            // Section 64: the project's setup, on the project.
            var project = await database.Projects.AsNoTracking().SingleAsync(one => one.Name == projectName);
            var setup = await database.WorkItems.AsNoTracking()
                .Where(one => one.ProjectId == project.Id)
                .Select(one => one.Title)
                .ToListAsync();

            Assert.Equal(5, setup.Count);
            Assert.Contains($"Create the repository for {projectName} and connect it here", setup);

            // Section 65: IT's work, the checklist, and the joiner's manager, who is given the
            // decision about their team.
            var joiner = await database.Employees.AsNoTracking().SingleAsync(one => one.FullName == joinerName);

            Assert.Contains($"Create accounts for {joinerName}, who starts on 2 Nov 2026", titles);

            var teamWork = await database.WorkItems.AsNoTracking()
                .SingleAsync(one => one.Title == $"Put {joinerName} on a team and confirm their manager");
            Assert.Equal(head, teamWork.AssigneeId);

            var checklist = await database.Onboardings.AsNoTracking().SingleAsync(one => one.EmployeeId == joiner.Id);
            Assert.Contains(checklist.Steps, step => step.Name == "Git host account created and added to the firm");

            // And everybody who should hear of it has.
            async Task Told(Guid who, string subject) => Assert.True(
                await database.Notices.AnyAsync(one => one.ForEmployeeId == who
                    && one.Kind == NoticeKind.Automation && one.Subject.StartsWith(subject)),
                $"Nobody told {who} \"{subject}\"");

            await Told(seller, clientName);
            await Told(finance, clientName);
            await Told(hrPerson, joinerName);
            await Told(it, "Accounts and equipment are needed for " + joinerName);
            await Told(head, joinerName + " joins your team");
        });
    }
}

public class AutomationWorkflowTests
{
    [Fact]
    public async Task The_shipped_rules_run_when_a_client_a_project_and_a_joiner_arrive()
    {
        using var factory = new AutomationFactory();

        await AutomationWorkflow.WalkAsync(factory);
    }

    [PostgresFact]
    public async Task The_shipped_rules_run_when_a_client_a_project_and_a_joiner_arrive_on_postgres()
    {
        using var factory = new AutomationPostgresFactory();

        await AutomationWorkflow.WalkAsync(factory);
    }
}
