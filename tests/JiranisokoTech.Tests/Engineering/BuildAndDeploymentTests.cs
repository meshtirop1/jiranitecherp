using System.Security.Cryptography;
using System.Text;
using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Engineering;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.Engineering;
using JiranisokoTech.Infrastructure.People;
using JiranisokoTech.Infrastructure.Work;
using JiranisokoTech.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace JiranisokoTech.Tests.Engineering;

/// <summary>
/// A build and a deployment arriving, and what they are not allowed to do.
/// </summary>
/// <remarks>
/// Section 12 could show a task's branch, its commits and its pull request and then
/// stopped, so "was it built, did it go out" was a question this system could not
/// answer about work it otherwise knew everything about. These are the tests for the
/// link that closes it.
///
/// Two themes run through the whole file, and both are about restraint rather than
/// capability. A build resolves its work through the commit and never by re-reading
/// the branch, because a person may have corrected that link by hand and nothing
/// automatic is entitled to overrule them. And neither a build nor a deployment moves
/// a work item, however conclusive it looks, because the board is a record of
/// decisions people made and a pipeline is not a person.
///
/// They run against a real database and through the real inbox and dispatcher,
/// because the guarantees being asserted are upsert guarantees — one row after three
/// deliveries — and an upsert cannot be demonstrated against a fake that has no rows.
/// </remarks>
public class BuildAndDeploymentTests
{
    private const string Secret = "the-secret-github-also-holds";

    private const string Built = "9a1c0ff4e6b3d2a18f7c5e0b4d3a2916f8e7c0d5";

    /// <summary>A commit this system was never told about.</summary>
    private const string Unknown = "1f2e3d4c5b6a79880123456789abcdef01234567";

    /// <summary>
    /// A build of a commit lands on the work that commit belongs to.
    /// </summary>
    /// <remarks>
    /// The whole of what section 12 was missing, in one test. Nobody typed anything:
    /// a developer named a branch after the task, pushed, the pipeline ran, and the
    /// answer to "did it build" appeared on the task without anybody carrying it
    /// there. If this breaks, the builds panel on the work item page is permanently
    /// empty and the feature looks like it was never wired up, because an unattached
    /// build fails nothing and logs nothing — it simply belongs to no task.
    /// </remarks>
    [Fact]
    public async Task A_build_is_attached_to_the_work_its_commit_belongs_to()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var work = await GiveWork(fixture, number: 412);
        await Connect(fixture);

        await Receive(fixture, "push", Push("feature/412-payment-api"), "d-1");
        await Dispatch(fixture);

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));

        await Receive(fixture, "workflow_run", WorkflowRun("completed", "success"), "d-2");
        await Dispatch(fixture);

        await using var context = fixture.NewContext();
        var build = await context.Builds.SingleAsync();

        Assert.Equal(work, build.WorkItemId);
        Assert.Equal(BuildOutcome.Passed, build.Outcome);
        Assert.Equal(Built, build.Sha);
        Assert.NotNull(build.FinishedAt);
    }

    /// <summary>
    /// The link comes from the commit, not from reading the branch a second time.
    /// </summary>
    /// <remarks>
    /// The commit here sits on a branch that parses to one task and is stored against
    /// another, which is what a work item page looks like the moment somebody notices
    /// a branch was misnamed and moves the commit by hand. Deriving the link again
    /// from the branch would silently undo that correction on the next build — and it
    /// would keep undoing it, on every build of every commit on that branch, while the
    /// person who made the correction watched it revert with nothing to read about
    /// why. A wrong attachment is the expensive kind of wrong here: it is invisible,
    /// and it puts evidence of work against a task nobody did.
    /// </remarks>
    [Fact]
    public async Task A_build_follows_the_commits_link_and_not_the_branch_name()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var named = await GiveWork(fixture, number: 77);
        var corrected = await GiveWork(fixture, number: 412);
        await Connect(fixture);

        await using (var context = fixture.NewContext())
        {
            var repository = await context.Repositories.SingleAsync();

            // The branch says 77 and the stored link says 412: a person moved this
            // commit on the work item page, which is the only way the two disagree.
            context.Commits.Add(Commit.Record(
                repository.Id, Built, "Add the retry", "meshtirop1", "feature/77-payment-api",
                corrected, fixture.Clock.Now));

            await context.SaveChangesAsync();
        }

        await Receive(
            fixture,
            "workflow_run",
            WorkflowRun("completed", "success", branch: "feature/77-payment-api"),
            "d-1");

        await Dispatch(fixture);

        await using var after = fixture.NewContext();
        var build = await after.Builds.SingleAsync();

        Assert.Equal(corrected, build.WorkItemId);
        Assert.NotEqual(named, build.WorkItemId);
    }

    /// <summary>
    /// With no commit to ask, the branch is the fallback rather than nothing.
    /// </summary>
    /// <remarks>
    /// A build genuinely arrives for a commit this system never saw: the repository
    /// was connected after the branch was pushed, or the push delivery is still
    /// dead-lettered. Losing the link entirely in that case would leave a task blank
    /// for the whole of the week somebody spent fixing the push, and the branch name
    /// is the same evidence the push itself would have been read for.
    /// </remarks>
    [Fact]
    public async Task A_build_with_no_commit_falls_back_to_the_branch()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var work = await GiveWork(fixture, number: 412);
        await Connect(fixture);

        await Receive(
            fixture, "workflow_run", WorkflowRun("completed", "success", sha: Unknown), "d-1");

        await Dispatch(fixture);

        await using var context = fixture.NewContext();

        Assert.Empty(await context.Commits.ToListAsync());
        Assert.Equal(work, (await context.Builds.SingleAsync()).WorkItemId);
    }

    /// <summary>
    /// A build nothing can be attached to is recorded unattached, never refused.
    /// </summary>
    /// <remarks>
    /// <c>release/2026-412</c> is deliberately not a work reference — WorkReference
    /// refuses it, because a number after a dash and more digits is a date or a
    /// version somewhere in the world. So this build has no commit and no readable
    /// branch, and the only question left is whether it is kept.
    ///
    /// It must be. Releases are cut from branches like this one and from <c>main</c>,
    /// which means the builds that matter most to whoever is watching a release are
    /// exactly the ones with no task to hang off. Refusing them would leave the
    /// repository's build history with a hole in it precisely on release day, and the
    /// delivery would fail five times and dead-letter on the way — turning an ordinary
    /// unattached build into an entry on the failures screen for somebody to
    /// investigate.
    /// </remarks>
    [Fact]
    public async Task A_build_with_nothing_to_attach_it_to_is_recorded_anyway()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await GiveWork(fixture, number: 412);
        await Connect(fixture);

        await Receive(
            fixture,
            "workflow_run",
            WorkflowRun("completed", "success", sha: Unknown, branch: "release/2026-412"),
            "d-1");

        await Dispatch(fixture);

        await using var context = fixture.NewContext();
        var build = await context.Builds.SingleAsync();

        Assert.Null(build.WorkItemId);
        Assert.Equal("release/2026-412", build.Branch);
        Assert.Equal(DeliveryStatus.Handled, (await context.Deliveries.SingleAsync()).Status);
    }

    /// <summary>
    /// A deployment from a branch that names no work is still a deployment.
    /// </summary>
    /// <remarks>
    /// The same rule as the build above and the case that occurs most often, because
    /// production is deployed from <c>main</c> and <c>main</c> names no task by
    /// design. The environments page exists to say what is live; dropping every
    /// release that came off the release branch would leave it saying nothing on the
    /// only days anybody opens it.
    /// </remarks>
    [Fact]
    public async Task A_deployment_from_a_branch_that_names_no_work_is_still_recorded()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await Connect(fixture);

        await Receive(fixture, "deployment", DeploymentCreated("production"), "d-1");
        await Dispatch(fixture);

        await using var context = fixture.NewContext();
        var deployment = await context.Deployments.SingleAsync();

        Assert.Null(deployment.WorkItemId);
        Assert.Equal("main", deployment.Branch);
        Assert.Equal(DeploymentEnvironment.Production, deployment.Environment);
        Assert.Equal(DeliveryStatus.Handled, (await context.Deliveries.SingleAsync()).Status);
    }

    /// <summary>
    /// One run reported three times is one build, and it stays finished.
    /// </summary>
    /// <remarks>
    /// GitHub reports a run when it starts and again when it ends, and re-sends
    /// anything it is unsure landed — so three deliveries about one run is the
    /// ordinary case rather than a pathology. Inserting on each would put three rows
    /// on a task for one run of one workflow, two of them claiming it is still going,
    /// with nothing on the screen to say which is current.
    ///
    /// The last delivery is the sharp one: it is the "started" body arriving after the
    /// "completed" one, which is what a manual redelivery or a retried webhook looks
    /// like. If it were allowed to write, a passed build would go back to running and
    /// stay there for ever, because nothing further is coming to correct it — and a
    /// work item page would show a build that has been in progress since last March.
    /// </remarks>
    [Fact]
    public async Task A_run_reported_three_times_is_one_build_and_stays_finished()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await GiveWork(fixture, number: 412);
        await Connect(fixture);

        await Receive(fixture, "workflow_run", WorkflowRun("in_progress", null), "d-1");
        await Dispatch(fixture);

        await using (var context = fixture.NewContext())
        {
            Assert.True((await context.Builds.SingleAsync()).IsRunning);
        }

        fixture.Clock.Advance(TimeSpan.FromMinutes(4));

        await Receive(fixture, "workflow_run", WorkflowRun("completed", "success"), "d-2");
        await Dispatch(fixture);

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));

        /*
         * The same body again under a new delivery identifier. That is what a
         * redelivery looks like from here: the inbox cannot tell it is stale, because
         * it genuinely is a second delivery, so the defence has to be the run's own
         * identifier and the aggregate refusing to reopen.
         */
        await Receive(fixture, "workflow_run", WorkflowRun("in_progress", null), "d-3");
        await Dispatch(fixture);

        await using var after = fixture.NewContext();
        var build = await after.Builds.SingleAsync();

        Assert.Equal(BuildOutcome.Passed, build.Outcome);
        Assert.NotNull(build.FinishedAt);
        Assert.Equal(3, await after.Deliveries.CountAsync());

        // Raised on the transition and nowhere else, so a handler added later can rely
        // on seeing a finished build once rather than once per delivery.
        Assert.Equal(1, await after.Outbox.CountAsync(one => one.Type == nameof(BuildFinished)));
    }

    /// <summary>
    /// A deployment and the status that settles it are one row.
    /// </summary>
    /// <remarks>
    /// GitHub sends a deployment event and then one or more deployment_status events
    /// for the same deployment, all carrying the same identifier. Insert-only would
    /// put every release on the environments page three or four times — on the one
    /// screen in this system whose entire job is to answer "what is live", where four
    /// rows for one release is not a cosmetic problem but an answer nobody can read.
    ///
    /// The redelivered "created" at the end is the same guard as on a build: a live
    /// release must not go back to in-progress because two deliveries arrived in the
    /// order the network chose.
    /// </remarks>
    [Fact]
    public async Task A_deployment_and_the_status_that_settles_it_are_one_row()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await Connect(fixture);

        await Receive(fixture, "deployment", DeploymentCreated("production"), "d-1");
        await Dispatch(fixture);

        await using (var context = fixture.NewContext())
        {
            Assert.True((await context.Deployments.SingleAsync()).IsRunning);
        }

        fixture.Clock.Advance(TimeSpan.FromMinutes(4));

        await Receive(
            fixture, "deployment_status", DeploymentStatus("production", "success"), "d-2");
        await Dispatch(fixture);

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));

        await Receive(fixture, "deployment", DeploymentCreated("production"), "d-3");
        await Dispatch(fixture);

        await using var after = fixture.NewContext();
        var deployment = await after.Deployments.SingleAsync();

        Assert.Equal(DeploymentState.Succeeded, deployment.State);
        Assert.True(deployment.Live);
        Assert.NotNull(deployment.FinishedAt);
        Assert.Equal(3, await after.Deliveries.CountAsync());

        Assert.Equal(
            1, await after.Outbox.CountAsync(one => one.Type == nameof(DeploymentFinished)));
    }

    /// <summary>
    /// The host's own name for an environment survives being classified.
    /// </summary>
    /// <remarks>
    /// Both facts are kept because they answer different questions. The enum is what a
    /// screen groups and counts by; the text is what somebody actually typed into a
    /// workflow file. Telling a firm "Production" when their pipeline said
    /// <c>prod-eu</c> is slightly untrue on the one page where being approximately
    /// right is worst — they have three production environments, and the page would no
    /// longer say which of them this release reached.
    /// </remarks>
    [Fact]
    public void An_environment_keeps_the_name_the_host_gave_it()
    {
        var deployment = Deployment.Record(
            Guid.CreateVersion7(), "2938475610", "prod-eu", Built,
            at: new DateTimeOffset(2026, 9, 22, 17, 0, 0, TimeSpan.Zero));

        Assert.Equal(DeploymentEnvironment.Production, deployment.Environment);
        Assert.Equal("prod-eu", deployment.EnvironmentName);
    }

    /// <summary>
    /// An environment nobody recognises is Other, and never a guess at Production.
    /// </summary>
    /// <remarks>
    /// The two failures are not comparable. Filing an unknown environment under Other
    /// costs a row in a group called Other, which somebody can look at and correct.
    /// Guessing it into Production costs a screen claiming something is live when it is
    /// not, which is the sentence people make release decisions on — and nothing about
    /// the row would look wrong.
    /// </remarks>
    [Theory]
    [InlineData("production", DeploymentEnvironment.Production)]
    [InlineData("prod-eu", DeploymentEnvironment.Production)]
    [InlineData("live", DeploymentEnvironment.Production)]
    [InlineData("staging", DeploymentEnvironment.Staging)]
    [InlineData("pre-prod", DeploymentEnvironment.Staging)]
    [InlineData("uat", DeploymentEnvironment.Development)]
    [InlineData("qa-2", DeploymentEnvironment.Development)]
    [InlineData("client-demo", DeploymentEnvironment.Other)]
    [InlineData("canary", DeploymentEnvironment.Other)]
    [InlineData("nairobi", DeploymentEnvironment.Other)]
    public void An_environment_is_classified_or_left_alone(
        string name, DeploymentEnvironment expected) =>
        Assert.Equal(expected, Deployment.Classify(name));

    /// <summary>
    /// A settled build ignores a later ending instead of throwing about it.
    /// </summary>
    /// <remarks>
    /// The guard is a return rather than an exception, and the difference is not a
    /// matter of taste. A throw here is caught by the dispatcher, counted as a failed
    /// attempt, and after five of them the delivery is dead-lettered — so using an
    /// exception to say "this one is already finished" turns an entirely ordinary
    /// redelivery into a red entry on the deliveries screen, which somebody then
    /// spends an afternoon investigating before concluding that nothing was wrong.
    ///
    /// The second assertion matters as much as the first: the original finishing time
    /// must survive, because a redelivery arriving an hour later would otherwise
    /// rewrite a four-minute build into a sixty-four-minute one.
    /// </remarks>
    [Fact]
    public void A_settled_build_ignores_a_later_ending()
    {
        var started = new DateTimeOffset(2026, 9, 22, 14, 5, 0, TimeSpan.Zero);

        var build = Build.Record(
            Guid.CreateVersion7(), "10592837465-1", "CI", Built, "feature/412-payment-api",
            null, BuildOutcome.Running, started);

        build.Ended(BuildOutcome.Passed, started.AddMinutes(4), "https://github.com/run/1");

        build.Ended(BuildOutcome.Running, started.AddHours(1));
        build.Ended(BuildOutcome.Failed, started.AddHours(1));

        Assert.Equal(BuildOutcome.Passed, build.Outcome);
        Assert.Equal(started.AddMinutes(4), build.FinishedAt);
        Assert.Equal(TimeSpan.FromMinutes(4), build.Took);
    }

    /// <summary>
    /// A settled deployment ignores a later ending, for the same reason.
    /// </summary>
    /// <remarks>
    /// Identical reasoning to the build, with a sharper consequence: the row this
    /// protects is the one the environments page reads to say what is live, so
    /// reopening it would leave a released version showing as still going out, with
    /// nothing further coming to settle it.
    /// </remarks>
    [Fact]
    public void A_settled_deployment_ignores_a_later_ending()
    {
        var at = new DateTimeOffset(2026, 9, 22, 17, 0, 0, TimeSpan.Zero);

        var deployment = Deployment.Record(
            Guid.CreateVersion7(), "2938475610", "production", Built, "main",
            state: DeploymentState.Running, at: at);

        deployment.Ended(DeploymentState.Succeeded, at.AddMinutes(4));

        deployment.Ended(DeploymentState.Running, at.AddHours(1));
        deployment.Ended(DeploymentState.Failed, at.AddHours(1));

        Assert.Equal(DeploymentState.Succeeded, deployment.State);
        Assert.True(deployment.Live);
        Assert.Equal(at.AddMinutes(4), deployment.FinishedAt);
    }

    /// <summary>
    /// A build announces that it finished exactly once.
    /// </summary>
    /// <remarks>
    /// Handlers run from the outbox and delivery there is already at least once, so an
    /// aggregate that raised the same event on every redelivery would multiply a
    /// problem the plumbing has enough of. Asserted on the aggregate rather than
    /// through the database because this is a promise the type makes to anybody who
    /// subscribes later: seeing BuildFinished means the build just changed, not that
    /// somebody's webhook was retried.
    /// </remarks>
    [Fact]
    public void A_build_announces_its_finish_once()
    {
        var started = new DateTimeOffset(2026, 9, 22, 14, 5, 0, TimeSpan.Zero);

        var build = Build.Record(
            Guid.CreateVersion7(), "10592837465-1", "CI", Built, "feature/412-payment-api",
            null, BuildOutcome.Running, started);

        Assert.Single(build.Events.OfType<BuildRecorded>());
        Assert.Empty(build.Events.OfType<BuildFinished>());

        build.Ended(BuildOutcome.Failed, started.AddMinutes(4));
        build.Ended(BuildOutcome.Failed, started.AddMinutes(4));

        Assert.Single(build.Events.OfType<BuildFinished>());
    }

    /// <summary>
    /// A deployment announces that it finished exactly once.
    /// </summary>
    /// <remarks>
    /// The same promise as a build's, and worth asserting separately because the two
    /// aggregates repeat the rule rather than share it — which is how one of them comes
    /// to lose it in a later edit while the other keeps it.
    /// </remarks>
    [Fact]
    public void A_deployment_announces_its_finish_once()
    {
        var at = new DateTimeOffset(2026, 9, 22, 17, 0, 0, TimeSpan.Zero);

        var deployment = Deployment.Record(
            Guid.CreateVersion7(), "2938475610", "production", Built, "main", at: at);

        Assert.Single(deployment.Events.OfType<DeploymentRecorded>());
        Assert.Empty(deployment.Events.OfType<DeploymentFinished>());

        deployment.Ended(DeploymentState.Succeeded, at.AddMinutes(4));
        deployment.Ended(DeploymentState.Succeeded, at.AddMinutes(4));

        Assert.Single(deployment.Events.OfType<DeploymentFinished>());
    }

    /// <summary>
    /// Neither a failed build nor a successful release moves the work item.
    /// </summary>
    /// <remarks>
    /// The most tempting feature in this section and the one that must not be built.
    /// WorkItemStatus.Deployed is reachable only from Done, and nothing leaves it —
    /// deliberately, because it records that work has left the building. So a
    /// deployment allowed to move a task there would permanently close it on the
    /// strength of one mis-parsed branch reference, with no route back for anybody, and
    /// the only remedy would be somebody editing the database by hand.
    ///
    /// The failed build is the other half of the same rule. Moving a task backwards
    /// because a pipeline went red would mean a flaky test reopening finished work
    /// overnight, and the board would stop being a record of what people decided.
    ///
    /// Both events are genuinely delivered here rather than stubbed, so this fails the
    /// day somebody wires something up to act on them.
    /// </remarks>
    [Fact]
    public async Task Neither_a_build_nor_a_deployment_moves_the_work()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var work = await GiveWork(fixture, number: 412, started: true);
        await Connect(fixture);

        await Receive(fixture, "push", Push("feature/412-payment-api"), "d-1");
        await Dispatch(fixture);

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));

        await Receive(fixture, "workflow_run", WorkflowRun("completed", "failure"), "d-2");
        await Dispatch(fixture);

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));

        await Receive(fixture, "deployment", DeploymentCreated("production"), "d-3");
        await Dispatch(fixture);

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));

        await Receive(
            fixture, "deployment_status", DeploymentStatus("production", "success"), "d-4");
        await Dispatch(fixture);

        await using var context = fixture.NewContext();

        // Both things really happened, so the assertion below is about restraint rather
        // than about nothing having been recorded.
        Assert.Equal(BuildOutcome.Failed, (await context.Builds.SingleAsync()).Outcome);
        Assert.Equal(
            DeploymentState.Succeeded, (await context.Deployments.SingleAsync()).State);

        var item = await context.WorkItems.SingleAsync(one => one.Id == work);

        Assert.Equal(WorkItemStatus.InProgress, item.Status);
    }

    /// <summary>
    /// Nothing in the system subscribes to a build or a deployment finishing.
    /// </summary>
    /// <remarks>
    /// Asserted on the shape of the code because the test above can only show that
    /// nothing acts on these events through the path it exercises. A handler registered
    /// later would run from the outbox, outside that path, and the first anybody would
    /// know about it is a closed work item that nobody closed.
    ///
    /// This is a decision and not an omission, so when it is reversed it should be
    /// reversed on purpose: whoever adds the handler deletes this test and writes down
    /// why in the same commit.
    /// </remarks>
    [Fact]
    public void Nothing_subscribes_to_a_build_or_a_deployment_finishing()
    {
        var handled = new[]
            {
                typeof(DeliveryDispatcher).Assembly,
                typeof(EngineeringRepository).Assembly,
            }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsAbstract: false, IsGenericTypeDefinition: false })
            .SelectMany(type => type.GetInterfaces())
            .Where(contract => contract.IsGenericType
                && contract.GetGenericTypeDefinition() == typeof(IDomainEventHandler<>))
            .Select(contract => contract.GetGenericArguments()[0])
            .ToList();

        Assert.DoesNotContain(typeof(BuildFinished), handled);
        Assert.DoesNotContain(typeof(DeploymentFinished), handled);
    }

    // --- the scaffolding ----------------------------------------------------

    private static async Task<Guid> GiveWork(
        DatabaseFixture fixture, int number, bool started = false)
    {
        await using var context = fixture.NewContext();

        var item = WorkItem.Raise(number, "Payment API retries", Guid.CreateVersion7());

        if (started)
        {
            item.MoveTo(WorkItemStatus.InProgress, fixture.Clock.Now);
        }

        context.WorkItems.Add(item);
        await context.SaveChangesAsync();

        return item.Id;
    }

    private static async Task Connect(DatabaseFixture fixture)
    {
        await using var context = fixture.NewContext();

        await new EngineeringService(
                new EngineeringRepository(context),
                new WorkRepository(context),
                new PeopleRepository(context),
                new Secrets(),
                fixture.Clock)
            .ConnectAsync(GitProvider.GitHub, "jiranisokotech", "erp", Secret);
    }

    private static async Task Receive(
        DatabaseFixture fixture, string eventName, string payload, string deliveryId)
    {
        await using var context = fixture.NewContext();

        var inbox = new WebhookInbox(
            new EngineeringRepository(context),
            [new GitHubProvider()],
            new Secrets(),
            fixture.Clock,
            NullLogger<WebhookInbox>.Instance);

        var body = Encoding.UTF8.GetBytes(payload);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-GitHub-Event"] = eventName,
            ["X-GitHub-Delivery"] = deliveryId,
            ["X-Hub-Signature-256"] = "sha256=" + Convert.ToHexStringLower(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), body)),
        };

        var receipt = await inbox.ReceiveAsync(GitProvider.GitHub, headers, body);

        // Asserted here rather than in each test, because a payload this file got wrong
        // would otherwise surface as an empty table and read as a fault in the
        // dispatcher rather than in the scaffolding.
        Assert.Equal(Reception.Accepted, receipt.Outcome);
    }

    private static async Task Dispatch(DatabaseFixture fixture)
    {
        await using var context = fixture.NewContext();

        await new DeliveryDispatcher(
                new EngineeringRepository(context),
                new WorkRepository(context),
                [new GitHubProvider()],
                fixture.Clock,
                NullLogger<DeliveryDispatcher>.Instance)
            .RunOnceAsync();
    }

    private static string Push(string branch) => $$"""
        {
          "ref": "refs/heads/{{branch}}",
          "repository": { "full_name": "jiranisokotech/erp" },
          "commits": [
            {
              "id": "{{Built}}",
              "message": "Add the retry",
              "timestamp": "2026-09-22T14:03:11+03:00",
              "author": { "name": "Meshack Tirop", "username": "meshtirop1" }
            }
          ]
        }
        """;

    /// <summary>
    /// One run of one workflow, as GitHub reports it at each stage of its life.
    /// </summary>
    /// <remarks>
    /// The status and the conclusion are separate arguments because that is how the
    /// payload carries them, and because the pair is the thing worth exercising: a run
    /// in progress carries a null conclusion, and reading that as a failure would put a
    /// red mark on every build for the quarter of an hour it was running.
    /// </remarks>
    private static string WorkflowRun(
        string status,
        string? conclusion,
        string sha = Built,
        string branch = "feature/412-payment-api")
    {
        var settled = conclusion is null ? "null" : $"\"{conclusion}\"";

        return $$"""
            {
              "action": "{{status}}",
              "repository": { "full_name": "jiranisokotech/erp" },
              "workflow_run": {
                "id": 10592837465,
                "run_attempt": 1,
                "name": "CI",
                "head_sha": "{{sha}}",
                "head_branch": "{{branch}}",
                "status": "{{status}}",
                "conclusion": {{settled}},
                "run_started_at": "2026-09-22T14:05:00Z",
                "updated_at": "2026-09-22T14:09:12Z",
                "html_url": "https://github.com/jiranisokotech/erp/actions/runs/10592837465"
              }
            }
            """;
    }

    private static string DeploymentCreated(string environment, string sha = Built) => $$"""
        {
          "action": "created",
          "repository": { "full_name": "jiranisokotech/erp" },
          "deployment": {
            "id": 2938475610,
            "environment": "{{environment}}",
            "sha": "{{sha}}",
            "ref": "main",
            "created_at": "2026-09-22T17:00:00Z",
            "url": "https://api.github.com/repos/jiranisokotech/erp/deployments/2938475610",
            "creator": { "login": "meshtirop1" }
          }
        }
        """;

    /// <summary>
    /// A status about the deployment above, carrying the same identifier.
    /// </summary>
    /// <remarks>
    /// The identifier is the deployment's own and not the status's, deliberately: it is
    /// what makes the second delivery settle the first rather than add a row beside it.
    /// </remarks>
    private static string DeploymentStatus(
        string environment, string state, string sha = Built) => $$"""
        {
          "action": "created",
          "repository": { "full_name": "jiranisokotech/erp" },
          "deployment": {
            "id": 2938475610,
            "environment": "{{environment}}",
            "sha": "{{sha}}",
            "ref": "main",
            "created_at": "2026-09-22T17:00:00Z",
            "url": "https://api.github.com/repos/jiranisokotech/erp/deployments/2938475610",
            "creator": { "login": "meshtirop1" }
          },
          "deployment_status": {
            "id": 5647382910,
            "state": "{{state}}",
            "updated_at": "2026-09-22T17:04:12Z",
            "target_url": "https://jiranisokotech.co.ke"
          }
        }
        """;

    /// <summary>The configured secret, as a stub.</summary>
    private sealed class Secrets : IWebhookSecrets
    {
        public string? For(GitProvider provider) => Secret;
    }
}
