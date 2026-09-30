using System.Net;
using JiranisokoTech.Application.Engineering;
using JiranisokoTech.Application.Incidents;
using JiranisokoTech.Application.People;
using JiranisokoTech.Application.Platform;
using JiranisokoTech.Application.Work;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Domain.Incidents;
using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Tests.Identity;
using JiranisokoTech.Tests.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Workflows;

/// <summary>
/// Section 93's chain, walked once: something is wrong, and it ends as a corrective action
/// somebody owns on the board.
/// </summary>
/// <remarks>
/// <b>This is the fourth of the brief's critical workflows and the only one that had no
/// end-to-end test.</b> Delivery, hiring and finance each had a walk; incidents had nineteen
/// service-level tests in <c>IncidentTests</c> and a chain that had only ever been walked by hand
/// in the running application. Its checklist row said so, which is why this exists.
///
/// It also closes a smaller gap that is easy to miss: nothing in the suite had ever GOT
/// <c>/incidents/{number}</c> or <c>/incidents/{number}/review</c>. <c>EveryPageOpensTests</c>
/// deliberately skips parameterised routes, so those two pages had never been rendered by any
/// test at all — a null reference in either would have shipped.
///
/// <b>The screens are driven through their services, not pressed.</b> All three incident pages
/// are <c>@rendermode InteractiveServer</c>, because they are used while things are on fire and a
/// page that reloaded between timeline lines is a page people keep a text file beside instead. So
/// there are no EditForms to post: <c>Browsing.PressAsync</c> would fail on its own assertion
/// that the page has no form of that name. The walk calls what each control calls and then GETs
/// the page to prove the result is rendered.
///
/// <b>Three of the brief's steps stay missing and this walk does not pretend otherwise.</b> There
/// is no monitoring or webhook intake — an incident is raised only by a person, and section 14
/// says there is no monitoring. The deployment that FIXED it is not linked to the incident;
/// "what changed just before" lists suspects from before it started and nothing records what went
/// out to end it. And verification has no step of its own; resolving is the closest thing.
/// </remarks>
public static class IncidentWorkflow
{
    private const string Sha = "9f2c41ab7d05e83c6b1f4a29d7e60b5c8a3f1d42";

    public static async Task WalkAsync(ApplicationFactory factory)
    {
        var tag = Guid.CreateVersion7().ToString("N")[^8..];

        // --- the one person who runs this, with a staff record behind the account ------------
        var email = $"devops-{tag}@jiranisokotech.co.ke";
        Guid engineer = default;

        await factory.InScopeAsync(async services =>
        {
            var people = services.GetRequiredService<PeopleService>();

            var hired = await people.HireAsync(
                "Wanjiru " + tag, DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-1));

            await people.StartAsync(hired.Id);
            engineer = hired.Id;
        });

        /*
         * One persona for the whole chain, and that is true to life rather than a shortcut: a
         * DevOps engineer holds repos.manage, incidents.raise, incidents.run, platform.manage and
         * flags.set, which is exactly the set this walk needs. Splitting it across three accounts
         * would be testing the permission matrix, which EnforcementTests already does.
         */
        var browser = await Browsing.AsStaffAsync(factory, email, engineer, Roles.DevOpsEngineer);

        // --- what changed before it broke ----------------------------------------------------
        var repository = "jiranisokotech/payments-" + tag;

        await factory.InScopeAsync(services =>
            services.GetRequiredService<EngineeringService>().ConnectAsync(
                GitProvider.GitHub,
                "jiranisokotech",
                "payments-" + tag,
                Workflow.GitHubSecret));

        /*
         * A deployment delivered through the real endpoint, so that the suspects panel is proved
         * across a section boundary. IncidentTests exercises that query with hand-made Deployment
         * rows; nothing had ever put one there the way GitHub does and then found it on an
         * incident's screen.
         *
         * Timestamped Z, because that is what GitHub sends for deployment_status.updated_at. The
         * +03:00 that a Nairobi COMMIT carries is a different trap and the delivery walk already
         * covers it; inventing an offset here would be testing a thing the provider does not do.
         */
        var wentOut = DateTimeOffset.UtcNow.AddHours(-3);

        await Delivering.SignedAsync(
            factory, "deployment_status", Deployment(repository, wentOut), $"deploy-{tag}");

        await Delivering.SettleAsync(factory);

        /*
         * And a flag moved, BEFORE the incident is reported. FlagService stamps the movement with
         * the clock's now and the page's window ends at the incident's StartedAt, so a flag moved
         * afterwards is correctly absent from the panel — which would look exactly like the query
         * being broken.
         */
        await factory.InScopeAsync(async services =>
        {
            var flags = services.GetRequiredService<FlagService>();

            var added = await flags.AddAsync(
                "settlement-batching-" + tag, "Batch settlement callbacks");

            await flags.SetAsync(
                added.Id, DeploymentEnvironment.Production, true, "Reducing callback volume", null);

        });

        // --- somebody notices ----------------------------------------------------------------
        var began = DateTimeOffset.UtcNow.AddHours(-1);
        Guid incident = default;
        var number = 0;

        await factory.InScopeAsync(async services =>
        {
            var incidents = services.GetRequiredService<IncidentService>();

            var raised = await incidents.ReportAsync(
                "Settlement callbacks are timing out " + tag,
                IncidentSeverity.Major,
                began,
                engineer,
                affects: "Anybody waiting on a settlement confirmation");

            incident = raised.Id;
            number = raised.Number;

            await incidents.LeadAsync(incident, engineer, engineer);

            /*
             * Worse than it first looked, with a reason. Section 93 asks for severity to be
             * changeable and for the change to be accounted for, which is why the service takes
             * the why rather than leaving it to a note somebody may not write.
             */
            await incidents.ReclassifyAsync(
                incident, IncidentSeverity.Critical, "Every settlement is affected, not some",
                engineer);
        });

        // --- the timeline, including a line written after the fact ---------------------------
        await factory.InScopeAsync(async services =>
        {
            var incidents = services.GetRequiredService<IncidentService>();

            await incidents.NoteAsync(
                incident,
                "Callback queue depth climbing, oldest message four minutes",
                NoteKind.Observation,
                began.AddMinutes(2),
                engineer);

            /*
             * Back-dated on purpose. A timeline written while things are on fire always has a
             * line somebody remembers late, and the record has to be able to say when it HAPPENED
             * as well as when it was written — otherwise the reconstruction afterwards is a
             * reconstruction of the typing.
             */
            await incidents.NoteAsync(
                incident,
                "The first customer call actually came in before the alert",
                NoteKind.Observation,
                began.AddMinutes(-6),
                engineer);

            await incidents.NoteAsync(
                incident,
                "Turning the batching flag back off",
                NoteKind.Action,
                began.AddMinutes(9),
                engineer);

            /*
             * And the start moves earlier because of what that late line said. Section 93 asks
             * for when it began to be correctable, and this is the case that makes it necessary:
             * the customer call is evidence that it started before anybody was told.
             */
            await incidents.StartedAtAsync(incident, began.AddMinutes(-6), engineer);
        });

        // --- stopped, then ended --------------------------------------------------------------
        await factory.InScopeAsync(async services =>
        {
            var incidents = services.GetRequiredService<IncidentService>();

            /*
             * Two moments, not one. Mitigated is when it stopped hurting anybody and resolved is
             * when it was actually fixed, and a system that records only the second one reports
             * every incident as having lasted until the repair.
             */
            await incidents.MitigateAsync(
                incident, "Batching flag turned off; callbacks draining", began.AddMinutes(12),
                engineer);

            await incidents.ResolveAsync(
                incident,
                "The batching window held callbacks past the provider's timeout",
                began.AddMinutes(40),
                engineer);
        });

        // --- and the screens say so -----------------------------------------------------------
        var list = await Read(browser, "/incidents");

        Assert.Contains("Settlement callbacks are timing out " + tag, list);

        var page = await Read(browser, $"/incidents/{number}");

        Assert.Contains("Settlement callbacks are timing out " + tag, page);
        Assert.Contains("Every settlement is affected, not some", page);
        Assert.Contains("The first customer call actually came in before the alert", page);
        Assert.Contains("The batching window held callbacks past the provider&#x27;s timeout", page);

        /*
         * The two suspects panels, asserted on their table captions rather than the shared
         * heading above them. "What changed just before" is one h2 over both tables, so it proves
         * nothing about either; the captions are one per table and are what says the join
         * actually produced rows.
         */
        Assert.Contains("Changes before the incident began", page);
        Assert.Contains(Sha[..7], page);

        /*
         * The flag movement is checked against its query rather than against the page, and the
         * reason is a real property of the system rather than a convenience.
         *
         * FlagService.SetAsync stamps a movement with the clock's now and there is no overload
         * that back-dates one — correctly, because a flag movement is an observed fact rather
         * than a recollection. The panel's window ENDS at the incident's StartedAt, which this
         * walk deliberately corrects backwards to six minutes before the report. So a movement
         * made by a test against the real clock is always after the window closes, and no
         * ordering of these steps can put it inside.
         *
         * In production the two coincide, because an incident is reported while it is happening.
         * Here they cannot, and asserting the caption would mean giving up the back-dated start —
         * which is one of the brief's own steps and the more valuable half.
         */
        var moved = await factory.InRequestAsync(services =>
            services.GetRequiredService<FlagService>().MovedBetweenAsync(
                wentOut, DateTimeOffset.UtcNow.AddMinutes(1)));

        Assert.Contains(moved, one => one.Key == "settlement-batching-" + tag);

        // --- the review, and the thing the firm is going to do about it ------------------------
        await factory.InScopeAsync(async services =>
        {
            var incidents = services.GetRequiredService<IncidentService>();

            await incidents.ReviewAsync(incident, engineer);

            await incidents.WriteReviewAsync(
                incident,
                "Settlement callbacks were held in a batch past the provider's timeout.",
                "The batching window was set in seconds and the timeout in minutes, and nothing "
                + "compared them.",
                "A customer rang before the queue-depth alert fired.",
                "An alert on the oldest message in the queue rather than on its depth.");

            await incidents.ActAsync(
                incident, "Alert on oldest queued message, not queue depth " + tag, engineer);

            await incidents.AgreeReviewAsync(incident, engineer);
        });

        var review = await Read(browser, $"/incidents/{number}/review");

        Assert.Contains("The four questions", review);
        Assert.Contains("A customer rang before the queue-depth alert fired.", review);
        Assert.Contains("Alert on oldest queued message, not queue depth " + tag, review);

        /*
         * The end of the chain, and the point of the whole section: a corrective action is a real
         * work item on the board, not a line in a document nobody opens again. Asserted against
         * the work items table rather than against the review page, because the review page would
         * show its own copy either way.
         */
        await factory.InScopeAsync(async services =>
        {
            var database = services.GetRequiredService<AppDbContext>();

            var item = await database.WorkItems
                .AsNoTracking()
                .SingleAsync(one =>
                    one.Title == "Alert on oldest queued message, not queue depth " + tag);

            // Still open: a corrective action nobody has to do is a document, not an action.
            Assert.DoesNotContain(item.Status, WorkItem.Finished);

            var stored = await database.Incidents
                .AsNoTracking()
                .SingleAsync(one => one.Id == incident);

            Assert.Equal(IncidentStatus.Resolved, stored.Status);
            Assert.Equal(IncidentSeverity.Critical, stored.Severity);

            /*
             * Only the detection gap is asserted as a duration. ToMitigate and ToResolve are
             * measured from the corrected start to the moment each was recorded, and this walk
             * posts a webhook and drains two queues between them — so a wall-clock assertion
             * there would fail on a slow machine and pass on a fast one, which is worse than no
             * assertion.
             */
            Assert.NotNull(stored.MitigatedAt);
            Assert.NotNull(stored.ResolvedAt);

            /*
             * The corrected start, to the second, and not merely "before it was reported".
             *
             * The first version of this asserted StartedAt < ReportedAt, which was true the
             * moment the incident was raised an hour in the past and stayed true with the
             * correction disabled — proved by neutering StartedAtAsync and watching this pass.
             * An assertion that cannot fail is worse than none, because it reads as cover.
             */
            Assert.Equal(
                began.AddMinutes(-6).ToUnixTimeSeconds(),
                stored.StartedAt.ToUnixTimeSeconds());
        });
    }

    private static async Task<string> Read(HttpClient browser, string path)
    {
        var page = await browser.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        return await page.Content.ReadAsStringAsync();
    }

    /// <remarks>
    /// The shape GitHub sends for a deployment that has landed. The environment matters: the
    /// suspects panel is about what reached production, and a staging deploy on the same sha
    /// would be the wrong kind of evidence to put in front of somebody at two in the morning.
    /// </remarks>
    private static string Deployment(string repository, DateTimeOffset at) => $$"""
        {
          "action": "created",
          "repository": { "full_name": "{{repository}}" },
          "deployment": {
            "id": 8810432,
            "environment": "production",
            "sha": "{{Sha}}",
            "ref": "main",
            "created_at": "{{at.UtcDateTime:yyyy-MM-ddTHH:mm:ss}}Z",
            "url": "https://api.github.com/repos/{{repository}}/deployments/8810432",
            "creator": { "login": "wanjiru" }
          },
          "deployment_status": {
            "id": 99331,
            "state": "success",
            "updated_at": "{{at.UtcDateTime:yyyy-MM-ddTHH:mm:ss}}Z",
            "target_url": "https://pay.jiranisokotech.co.ke"
          }
        }
        """;
}

public class IncidentWorkflowTests
{
    [Fact]
    public async Task From_something_wrong_to_a_corrective_action_on_the_board()
    {
        await using var factory = new WorkflowFactory();

        await IncidentWorkflow.WalkAsync(factory);
    }

    /// <remarks>
    /// And again on PostgreSQL, because SQLite forgives two things it does not: a query that
    /// finishes before an await yields, and a timestamp carrying an offset. Both of those are in
    /// this walk.
    /// </remarks>
    [PostgresFact]
    public async Task From_something_wrong_to_a_corrective_action_on_the_board_on_postgres()
    {
        await using var factory = new WorkflowPostgresFactory();

        await IncidentWorkflow.WalkAsync(factory);
    }
}
