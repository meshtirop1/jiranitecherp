using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using JiranisokoTech.Application.Engineering;
using JiranisokoTech.Application.People;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Infrastructure.Messaging;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Tests.Identity;
using JiranisokoTech.Tests.Postgres;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Workflows;

/// <summary>
/// The brief's first end-to-end workflow, from an empty firm to work deployed.
/// </summary>
/// <remarks>
/// Section 46: organisation, invite, project, task, repository, webhook, pull request, merge,
/// deployment. Every step here had its own tests already, and none of them proved the steps
/// join: that the account an invitation opens can be the one the work is assigned to, that the
/// number a task is given is the one a branch name finds, that the secret a repository is
/// connected with is the one a delivery is checked against, and that a merge reaches the board
/// through the outbox rather than through a test calling the handler.
///
/// Each step is done by the person who would do it, through the page they would use — the
/// owner names the firm, HR invites and links, a delivery manager starts the project and
/// raises the work, an engineering manager connects the repository, the invited developer
/// starts the work. GitHub's part arrives at the real webhook endpoint, signed. Two things are
/// done through services, because no screen does them: hiring the staff record the invited
/// account is linked to (the hire page is its own chain, below), and draining the two queues,
/// which in production are hosted loops and here are run at the moment the chain needs them.
/// </remarks>
public static partial class DeliveryWorkflow
{
    private const string Sha = "5b1d6e0a7c3f9e2d8b4a1c6f0e9d3b7a2c8f4e1d";

    public static async Task WalkAsync(ApplicationFactory factory)
    {
        var tag = Guid.CreateVersion7().ToString("N")[^6..];

        // --- the organisation ------------------------------------------------------------
        var owner = await Browsing.SignedInAsync(factory, $"owner-{tag}@jiranisokotech.co.ke", Roles.Owner);

        Browsing.Accepted(await Browsing.PressAsync(owner, "/settings", "who", new Dictionary<string, string>
        {
            ["Who.TradingName"] = "Jiranisoko Tech",
            ["Who.LegalName"] = "Jiranisoko Tech Solutions Limited",
        }));

        await factory.InScopeAsync(async services =>
            Assert.Equal("Jiranisoko Tech Solutions Limited",
                (await services.GetRequiredService<AppDbContext>().Settings.AsNoTracking().SingleAsync()).LegalName));

        // --- HR invites the developer, who sets a password from the link ---------------------
        var hr = await Browsing.SignedInAsync(factory, $"hr-{tag}@jiranisokotech.co.ke", Roles.HumanResources);
        var developerEmail = $"wanjiru-{tag}@jiranisokotech.co.ke";

        var invited = await Browsing.PressAsync(hr, "/accounts", "invite", new Dictionary<string, string>
        {
            ["Input.DisplayName"] = "Wanjiru",
            ["Input.Email"] = developerEmail,
            ["Input.JobTitle"] = "Software engineer",
        });

        var link = WebUtility.HtmlDecode(
            SetPasswordLink().Match(await invited.Content.ReadAsStringAsync()).Groups[1].Value);

        Assert.Contains("/set-password", link);

        using (var invitee = factory.CreateBrowser())
        {
            var set = await Browsing.PressAsync(invitee, link, "set-password", new Dictionary<string, string>
            {
                ["Input.Password"] = Browsing.Password,
                ["Input.Again"] = Browsing.Password,
            });

            Assert.Contains("That is set", await set.Content.ReadAsStringAsync());
        }

        // The account is given a role and linked to a staff record, which is what lets work be
        // assigned to the person rather than to a login.
        var developerAccount = Guid.Empty;
        var developerRecord = Guid.Empty;
        var managerRecord = Guid.Empty;
        var managerEmail = $"otieno-{tag}@jiranisokotech.co.ke";

        await factory.InScopeAsync(async services =>
        {
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var people = services.GetRequiredService<PeopleService>();
            var account = (await users.FindByEmailAsync(developerEmail))!;

            await users.AddToRoleAsync(account, Roles.Developer);
            developerAccount = account.Id;

            var developer = await people.HireAsync($"Wanjiru {tag}", DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-7));
            await people.StartAsync(developer.Id);
            developerRecord = developer.Id;

            var manager = await people.HireAsync($"Otieno {tag}", DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-400));
            await people.StartAsync(manager.Id);
            managerRecord = manager.Id;
        });

        Browsing.Accepted(await Browsing.PressAsync(hr, $"/accounts/{developerAccount}", "link-staff", new Dictionary<string, string>
        {
            ["Linking.EmployeeId"] = developerRecord.ToString(),
        }));

        // --- a delivery manager starts the project and raises the work ------------------------
        var manager = await Browsing.SignedInAsync(factory, managerEmail, Roles.ProjectManager);

        await factory.InScopeAsync(async services =>
        {
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();

            await services.GetRequiredService<PeopleService>()
                .LinkAccountAsync(managerRecord, (await users.FindByEmailAsync(managerEmail))!.Id);

            // The link HR made through the page, checked here where it would first matter.
            Assert.Equal(developerAccount, (await services.GetRequiredService<AppDbContext>().Employees
                .AsNoTracking().SingleAsync(one => one.Id == developerRecord)).AccountId);
        });

        var projectName = $"Payments {tag}";

        Browsing.Accepted(await Browsing.PressAsync(manager, "/projects", "begin",
            new Dictionary<string, string> { ["Input.Name"] = projectName }));

        var project = Guid.Empty;

        await factory.InScopeAsync(async services =>
            project = (await services.GetRequiredService<AppDbContext>().Projects.AsNoTracking()
                .SingleAsync(one => one.Name == projectName)).Id);

        var raised = await Browsing.PressAsync(manager, "/work/new", "raise", new Dictionary<string, string>
        {
            ["Input.Title"] = "Retry failed M-Pesa callbacks",
            ["Input.ProjectId"] = project.ToString(),
            ["Input.AssigneeId"] = developerRecord.ToString(),

            // What a browser posts for the two selects; HtmlForm reads only inputs. Leaving
            // them out is how this test found the domain storing a kind that does not exist.
            ["Input.Kind"] = nameof(WorkItemKind.Task),
            ["Input.Priority"] = nameof(Priority.Normal),
        });

        Browsing.Accepted(raised);
        Assert.Equal(HttpStatusCode.Found, raised.StatusCode);

        var item = Guid.Parse(raised.Headers.Location!.OriginalString.Split('/')[^1]);
        var number = 0;

        await factory.InScopeAsync(async services =>
        {
            var work = await services.GetRequiredService<AppDbContext>().WorkItems.AsNoTracking()
                .SingleAsync(one => one.Id == item);

            Assert.Equal(project, work.ProjectId);
            Assert.Equal(developerRecord, work.AssigneeId);
            number = work.Number;
        });

        // --- an engineering manager connects the repository ------------------------------
        var engineering = await Browsing.SignedInAsync(factory, $"eng-{tag}@jiranisokotech.co.ke", Roles.EngineeringManager);
        var repositoryName = $"payments-{tag}";

        Browsing.Accepted(await Browsing.PressAsync(engineering, "/repositories", "connect", new Dictionary<string, string>
        {
            /*
             * The host is posted because a browser posts a select, and because leaving it out is
             * how this step first failed: a non-nullable enum bound from a form comes back as the
             * underlying zero when nothing is posted for it, and zero is no member of GitProvider.
             * The service now refuses that outright; before it did, the refusal was a complaint
             * about "Git:Providers:0:Secret".
             */
            ["Input.Provider"] = nameof(GitProvider.GitHub),
            ["Input.Owner"] = "jiranisokotech",
            ["Input.Name"] = repositoryName,
            ["Input.Secret"] = Workflow.GitHubSecret,
            ["Input.ProjectId"] = project.ToString(),
        }));

        await factory.InScopeAsync(async services =>
            Assert.True(await services.GetRequiredService<AppDbContext>().Repositories
                .AnyAsync(one => one.Name == repositoryName)));

        // --- the developer starts, and GitHub reports the rest --------------------------------
        var developer = await Browsing.SignedInAsync(factory, developerEmail);

        Browsing.Accepted(await Browsing.PressAsync(developer, $"/work/{item}", "move-InProgress"));

        var fullName = $"jiranisokotech/{repositoryName}";
        var branch = $"feature/{number}-mpesa-retries";

        await DeliverAsync(factory, "push", Push(fullName, branch), $"push-{tag}");
        await DeliverAsync(factory, "pull_request", PullRequest(fullName, branch, "opened", merged: false), $"pr-open-{tag}");
        await DrainAsync(factory);

        await factory.InScopeAsync(async services =>
        {
            var pull = await services.GetRequiredService<AppDbContext>().PullRequests.AsNoTracking()
                .SingleAsync(one => one.Branch == branch);

            Assert.Equal(PullRequestState.Open, pull.State);
            Assert.Equal(item, pull.WorkItemId);
        });

        await DeliverAsync(factory, "pull_request", PullRequest(fullName, branch, "closed", merged: true), $"pr-merge-{tag}");
        await DrainAsync(factory);

        await factory.InScopeAsync(async services =>
        {
            var database = services.GetRequiredService<AppDbContext>();

            Assert.Equal(PullRequestState.Merged,
                (await database.PullRequests.AsNoTracking().SingleAsync(one => one.Branch == branch)).State);

            // The merge reached the board through the outbox, which is the half no earlier test
            // walked: each called the handler directly.
            Assert.Equal(WorkItemStatus.InReview,
                (await database.WorkItems.AsNoTracking().SingleAsync(one => one.Id == item)).Status);
        });

        await DeliverAsync(factory, "deployment_status", Deployment(fullName, "success"), $"deploy-{tag}");
        await DrainAsync(factory);

        await factory.InScopeAsync(async services =>
        {
            var deployment = await services.GetRequiredService<AppDbContext>().Deployments.AsNoTracking()
                .SingleAsync(one => one.Sha == Sha);

            Assert.Equal(DeploymentState.Succeeded, deployment.State);

            // Found through the commit the push recorded against the work, not the branch name:
            // the deployment is of main.
            Assert.Equal(item, deployment.WorkItemId);
        });

        // And the work item page, as the developer sees it, says so.
        var page = await (await developer.GetAsync($"/work/{item}")).Content.ReadAsStringAsync();

        Assert.Contains(Sha[..7], page);

        /*
         * Section 71's stream, on the one page in the suite where every kind of line it can draw
         * has a real fact behind it: a move made through the board, a push, a pull request, a
         * merge that arrived through the outbox, and a deployment. The unit tests word the
         * sentences; this is the only thing that proves StoryFactsAsync's projection translates
         * through the work item's copy-returning Comments and DoneWhen navigations, and that the
         * component is in a folder Razor can resolve — a tag it cannot resolve is emitted as
         * unknown HTML and dropped by the browser, which is how the client page's attachments
         * section was a heading with nothing under it for weeks.
         */
        Assert.Contains("What has happened", page);
        Assert.Contains("was merged.", page);
        Assert.Contains("Reached production.", page);

        /*
         * And what the developer does NOT get. A Developer holds tasks.view_own and repos.view and
         * not audit.view, so the moves — which for InReview, Blocked and Deployed exist in no
         * column anywhere — are missing from their stream. The point of the assertion is the
         * paragraph: a stream that silently omitted them would read as work nobody had ever moved.
         */
        Assert.DoesNotContain("Moved to in review.", page);
        Assert.Contains("They are in the change trail, which", page);

        /*
         * The project manager holds all three, so the same page shows the moves and drops the
         * paragraph. Read as a second person rather than by granting the developer another role,
         * because the thing being checked is that the page answers to the permission and not that
         * the sentence can be made to appear.
         */
        var reading = await (await manager.GetAsync($"/work/{item}")).Content.ReadAsStringAsync();

        Assert.Contains("Moved to in review.", reading);
        Assert.DoesNotContain("They are in the change trail, which", reading);
        Assert.Contains($"/audit?type=WorkItem&amp;subject={item}", reading);
    }

    /// <remarks>
    /// Both of these moved to <see cref="Delivering"/> when the incident walk needed the same
    /// machinery. The names and signatures stay so that no step of this walk moved with them.
    /// </remarks>
    private static Task DeliverAsync(
        ApplicationFactory factory, string kind, string payload, string id) =>
        Delivering.SignedAsync(factory, kind, payload, id);

    private static Task DrainAsync(ApplicationFactory factory) =>
        Delivering.SettleAsync(factory);

    private static string Push(string repository, string branch) => $$"""
        {
          "ref": "refs/heads/{{branch}}",
          "repository": { "full_name": "{{repository}}" },
          "commits": [
            {
              "id": "{{Sha}}",
              "message": "Retry a callback that timed out",
              "timestamp": "2026-09-22T14:03:11+03:00",
              "author": { "name": "Wanjiru", "username": "wanjiru" }
            }
          ]
        }
        """;

    private static string PullRequest(string repository, string branch, string action, bool merged) => $$"""
        {
          "action": "{{action}}",
          "repository": { "full_name": "{{repository}}" },
          "pull_request": {
            "number": 18,
            "title": "Retry failed M-Pesa callbacks",
            "merged": {{(merged ? "true" : "false")}},
            "created_at": "2026-09-22T14:10:00Z",
            "closed_at": {{(merged ? "\"2026-09-22T16:45:00Z\"" : "null")}},
            "merged_at": {{(merged ? "\"2026-09-22T16:45:00Z\"" : "null")}},
            "head": { "ref": "{{branch}}" },
            "user": { "login": "wanjiru" }
          }
        }
        """;

    private static string Deployment(string repository, string state) => $$"""
        {
          "action": "created",
          "repository": { "full_name": "{{repository}}" },
          "deployment": {
            "id": 7740021,
            "environment": "production",
            "sha": "{{Sha}}",
            "ref": "main",
            "created_at": "2026-09-22T17:00:00Z",
            "url": "https://api.github.com/repos/{{repository}}/deployments/7740021",
            "creator": { "login": "otieno" }
          },
          "deployment_status": {
            "id": 99120,
            "state": "{{state}}",
            "updated_at": "2026-09-22T17:04:12Z",
            "target_url": "https://pay.jiranisokotech.co.ke"
          }
        }
        """;

    [GeneratedRegex("""<code class="link">([^<]+)</code>""")]
    private static partial Regex SetPasswordLink();
}

public class DeliveryWorkflowTests
{
    [Fact]
    public async Task From_an_empty_firm_to_work_deployed()
    {
        using var factory = new WorkflowFactory();

        await DeliveryWorkflow.WalkAsync(factory);
    }

    [PostgresFact]
    public async Task From_an_empty_firm_to_work_deployed_on_postgres()
    {
        using var factory = new WorkflowPostgresFactory();

        await DeliveryWorkflow.WalkAsync(factory);
    }
}
