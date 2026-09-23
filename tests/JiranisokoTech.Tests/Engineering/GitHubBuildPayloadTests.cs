using JiranisokoTech.Application.Engineering;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Infrastructure.Engineering;

namespace JiranisokoTech.Tests.Engineering;

/// <summary>
/// Reading what GitHub actually sends about a build and about a release.
/// </summary>
/// <remarks>
/// The sibling of <see cref="GitHubPayloadTests"/>, kept apart because the
/// failures it guards against are a different kind. A pull request read wrongly
/// puts a card in the wrong column, and somebody notices within the hour. A build
/// read wrongly puts a colour on a screen that people quietly learn to distrust,
/// and nobody ever files that as a bug — they simply stop looking at it.
///
/// The payloads are trimmed from real deliveries: the fields this system reads, in
/// the nesting GitHub puts them in, with a few of the neighbouring ones left in
/// place so the shape is recognisable to anybody holding this beside a delivery on
/// the repository's webhook screen.
///
/// Three cases here earn the whole file. A run still going carries a conclusion of
/// null, and reading that as a failure paints every build red for the quarter of an
/// hour it takes. A re-run keeps the run's identifier and changes only the attempt,
/// so a build keyed on the identifier alone throws its good news away as a
/// duplicate of the bad. And a workflow run's identifier is past eleven digits,
/// which no <c>int</c> will hold.
/// </remarks>
public class GitHubBuildPayloadTests
{
    private const string Sha = "9a1c0ff4e6b3d2a18f7c5e0b4d3a2916f8e7c0d5";

    /// <summary>A real Actions run identifier: eleven digits, and still climbing.</summary>
    private const long RunId = 10923847561;

    private const long DeploymentId = 1847263910;

    private readonly GitHubProvider _provider = new();

    /// <summary>
    /// A run that finished green is reported green, under the workflow's own name.
    /// </summary>
    /// <remarks>
    /// The name is half of what makes a build worth putting on a screen. A
    /// repository here runs several workflows against the same commit — tests,
    /// lint, a container build — so "the build passed" tells nobody which of them
    /// did, and three unnamed green marks are three facts nobody can act on. The
    /// commit and the branch are the other half: without both, the build is attached
    /// to nothing and appears on no work item at all.
    /// </remarks>
    [Fact]
    public void A_completed_run_that_succeeded_is_a_pass()
    {
        var report = ReadBuild(_provider.Read("workflow_run", """
            {
              "action": "completed",
              "workflow_run": {
                "id": 10923847561,
                "name": "CI",
                "node_id": "WFR_kwLOAbcDeM8AAAACgHqYaQ",
                "head_branch": "feature/412-payment-api",
                "head_sha": "9a1c0ff4e6b3d2a18f7c5e0b4d3a2916f8e7c0d5",
                "run_number": 318,
                "run_attempt": 1,
                "event": "push",
                "status": "completed",
                "conclusion": "success",
                "workflow_id": 148271,
                "html_url": "https://github.com/jiranisokotech/erp/actions/runs/10923847561",
                "run_started_at": "2026-09-22T14:05:02Z",
                "created_at": "2026-09-22T14:04:58Z",
                "updated_at": "2026-09-22T14:09:14Z"
              },
              "repository": { "full_name": "jiranisokotech/erp" }
            }
            """));

        Assert.Equal(BuildOutcome.Passed, report.Outcome);
        Assert.Equal("CI", report.Name);
        Assert.Equal(Sha, report.Sha);
        Assert.Equal("feature/412-payment-api", report.Branch);

        // run_started_at and not created_at. The gap between the two is the time the
        // job spent queued behind somebody else's run, and counting that as part of
        // the build would make a busy afternoon look like a slow pipeline.
        Assert.Equal(
            new DateTimeOffset(2026, 9, 22, 14, 5, 2, TimeSpan.Zero), report.StartedAt);

        Assert.Equal(
            new DateTimeOffset(2026, 9, 22, 14, 9, 14, TimeSpan.Zero), report.FinishedAt);

        // The link to the log, which is the only thing this system has to offer
        // somebody whose build has just failed.
        Assert.Equal(
            "https://github.com/jiranisokotech/erp/actions/runs/10923847561", report.Url);
    }

    /// <summary>
    /// A run that is still going is running, and has not finished.
    /// </summary>
    /// <remarks>
    /// The test this file was written for. GitHub sends a conclusion of null for the
    /// whole time a run is in progress, so a reader that looks at the conclusion
    /// before the status — the obvious order, and the one somebody will reach for
    /// while tidying this up — finds null, falls into its default case and reports a
    /// failure. Every build on the repository would then show red for the fifteen
    /// minutes it took and correct itself only on completion, which means telling
    /// people several times a day that their work is broken when the pipeline is
    /// merely busy.
    ///
    /// The finished time has to stay absent for exactly as long. A run that claims to
    /// have ended while it is still going hands the build panel a duration for
    /// something that has not happened yet.
    /// </remarks>
    [Fact]
    public void A_run_still_going_is_running_and_has_not_finished()
    {
        var report = ReadBuild(
            _provider.Read("workflow_run", WorkflowRun("in_progress", null)));

        Assert.Equal(BuildOutcome.Running, report.Outcome);
        Assert.Null(report.FinishedAt);
    }

    /// <summary>
    /// A cancelled run is cancelled, and is not a failure.
    /// </summary>
    /// <remarks>
    /// A run is cancelled because somebody pushed again before it finished, or
    /// because the queue was drained, and neither says anything at all about the
    /// code. Filing it as a failure puts a red mark against work that was never
    /// broken — several times over on any day somebody is iterating quickly — and
    /// what people learn from a colour that is wrong that often is to stop reading
    /// it, which costs them the failures that were real.
    /// </remarks>
    [Fact]
    public void A_cancelled_run_is_not_counted_as_a_failure()
    {
        var report = ReadBuild(
            _provider.Read("workflow_run", WorkflowRun("completed", "cancelled")));

        Assert.Equal(BuildOutcome.Cancelled, report.Outcome);
        Assert.NotEqual(BuildOutcome.Failed, report.Outcome);
    }

    /// <summary>
    /// Anything that is not a success is a failure, including a word GitHub has not
    /// invented yet.
    /// </summary>
    /// <remarks>
    /// The adapter matches the one conclusion that means green and treats everything
    /// else as red, rather than listing the failure words. The difference shows up
    /// only on the day GitHub adds a conclusion — they have added several — and on
    /// that day a list of failure words reads the new one as a pass by omission. A
    /// build reporting green when it is not is the single fault in this adapter that
    /// would cost somebody a release, so the default leans the safe way and this test
    /// is what holds it there when the list looks tempting to tidy.
    /// </remarks>
    [Theory]
    [InlineData("timed_out")]
    [InlineData("failure")]
    [InlineData("neutral")]
    [InlineData("action_required")]
    [InlineData("startup_failure")]
    [InlineData("a_conclusion_invented_after_this_was_written")]
    public void A_conclusion_that_is_not_a_success_is_read_as_a_failure(string conclusion)
    {
        var report = ReadBuild(
            _provider.Read("workflow_run", WorkflowRun("completed", conclusion)));

        Assert.Equal(BuildOutcome.Failed, report.Outcome);
        Assert.NotEqual(BuildOutcome.Passed, report.Outcome);
    }

    /// <summary>
    /// Pressing re-run produces a different build, not a redelivery of the old one.
    /// </summary>
    /// <remarks>
    /// GitHub keeps the run's identifier when somebody presses re-run and increments
    /// run_attempt instead. A build keyed on the identifier alone therefore matches
    /// the row the first attempt already settled, is dropped as a stale redelivery —
    /// which is the correct handling of an actual redelivery, so nothing appears in
    /// any log — and the work item page goes on showing a failure that was fixed and
    /// re-run twenty minutes ago. It stays that way for ever, because nothing further
    /// is coming to correct it.
    ///
    /// The person who pressed re-run and watched it go green is then reading a screen
    /// that disagrees with GitHub, which is the quickest way to lose their trust in
    /// the whole panel.
    /// </remarks>
    [Fact]
    public void A_re_run_is_a_different_build_from_the_attempt_it_replaces()
    {
        var failed = ReadBuild(_provider.Read(
            "workflow_run", WorkflowRun("completed", "failure", attempt: 1)));

        var rerun = ReadBuild(_provider.Read(
            "workflow_run", WorkflowRun("completed", "success", attempt: 2)));

        Assert.NotEqual(failed.ExternalId, rerun.ExternalId);

        // Both attempts are of the same commit, which is what keeps them together on
        // the work item instead of arriving as two unrelated builds.
        Assert.Equal(failed.Sha, rerun.Sha);
    }

    /// <summary>
    /// A run identifier past the range of an int is read rather than thrown over.
    /// </summary>
    /// <remarks>
    /// Actions run identifiers passed two billion some time ago and are now eleven
    /// digits. Reading one through the payload helper that returns an <c>int</c>
    /// fails its parse, falls through and raises; and because the delivery pipeline
    /// marks anything that raises as failed, that one line would have dead-lettered
    /// every build delivery on every repository, complaining about a missing number
    /// that was sitting in the payload the whole time. Identifier exists for this,
    /// and this test is the reason it may not be tidied back into a number.
    /// </remarks>
    [Fact]
    public void A_run_identifier_too_large_for_an_int_is_still_read()
    {
        Assert.True(RunId > int.MaxValue, "The payload must exercise the case it exists for.");

        var report = ReadBuild(
            _provider.Read("workflow_run", WorkflowRun("completed", "success")));

        Assert.Contains(RunId.ToString(), report.ExternalId);
    }

    /// <summary>
    /// The checks events are left alone, because workflow_run already reports the run.
    /// </summary>
    /// <remarks>
    /// GitHub sends check_suite and check_run for the very same Actions run that
    /// workflow_run describes, under different identifiers — so a reader that takes
    /// any second one of them records the build twice, and a work item shows "CI
    /// passed" beside "CI passed" with nothing on the page to say which is the
    /// duplicate. Nobody looking at it can tell whether the pipeline ran once or
    /// twice, and no amount of staring will settle the question.
    ///
    /// The reason is asserted and not only the refusal, because somebody reading the
    /// deliveries screen has to be told which event does the job. Otherwise a
    /// deliberately skipped delivery looks exactly like a webhook that is broken.
    /// </remarks>
    [Theory]
    [InlineData("check_suite")]
    [InlineData("check_run")]
    public void A_checks_event_is_left_to_workflow_run(string eventName)
    {
        var read = _provider.Read(eventName, $$"""
            {
              "action": "completed",
              "{{eventName}}": {
                "id": 38472619384,
                "head_sha": "{{Sha}}",
                "status": "completed",
                "conclusion": "success"
              },
              "repository": { "full_name": "jiranisokotech/erp" }
            }
            """);

        Assert.Contains("workflow_run", Assert.IsType<GitEvent.Uninteresting>(read).Why);
    }

    /// <summary>
    /// A successful deployment status says where the commit actually got to.
    /// </summary>
    /// <remarks>
    /// The environment name and the commit are the two halves of the only question
    /// anybody asks of this record: is the thing I wrote live. Either one missing
    /// turns the answer into a row saying something went somewhere.
    ///
    /// The time is the status's and not the deployment's, because the deployment's
    /// own timestamp is when the release began. Using it would make every release
    /// look instantaneous and throw away the four minutes it took, which is the one
    /// number worth watching on the day deploys start taking longer than they used
    /// to.
    /// </remarks>
    [Fact]
    public void A_successful_deployment_status_carries_the_environment_and_the_commit()
    {
        var report = ReadDeployment(_provider.Read("deployment_status", Status("success")));

        Assert.Equal(DeploymentState.Succeeded, report.State);
        Assert.Equal("production", report.EnvironmentName);
        Assert.Equal(Sha, report.Sha);
        Assert.Equal("meshtirop1", report.DeployedBy);

        // GitHub sends a deployment's ref as a bare branch name rather than as
        // refs/heads/…, so it is kept as it came instead of being discarded for
        // failing to look like a ref.
        Assert.Equal("main", report.Branch);

        Assert.Equal(new DateTimeOffset(2026, 9, 22, 17, 6, 41, TimeSpan.Zero), report.At);

        // target_url is the run that did the deploying, which is where somebody goes
        // when they want to know what actually happened.
        Assert.Equal(
            "https://github.com/jiranisokotech/erp/actions/runs/10923847561", report.Url);
    }

    /// <summary>
    /// A deployment marked inactive was superseded, and nothing went wrong.
    /// </summary>
    /// <remarks>
    /// GitHub marks the previous deployment of an environment inactive the moment a
    /// new one succeeds, so this state arrives after every single successful release.
    /// Read as a failure — which is where it lands the moment somebody treats
    /// anything that is not a success as one — the environments page would show a red
    /// mark against the release before last, permanently, immediately after each good
    /// deploy. The page would be at its most wrong precisely when everything was
    /// going well.
    /// </remarks>
    [Fact]
    public void A_deployment_marked_inactive_is_not_a_failure()
    {
        var read = _provider.Read("deployment_status", Status("inactive"));

        Assert.IsType<GitEvent.Uninteresting>(read);
    }

    /// <summary>
    /// A deployment and its status are one release, not two.
    /// </summary>
    /// <remarks>
    /// Both events are read because GitHub guarantees neither: a repository whose
    /// workflow never posts a status would record nothing at all if only statuses
    /// were read, and a row saying "going out, nothing heard since" is by far the more
    /// useful of the two silences. That only works while the two agree on the
    /// identifier. Keying the created event on the deployment's id and the status on
    /// the status's own id would put every release on the environments page twice,
    /// once stuck in progress for ever and once settled, with nothing to reconcile
    /// the pair by.
    /// </remarks>
    [Fact]
    public void A_deployment_and_its_status_are_the_same_release()
    {
        var created = ReadDeployment(_provider.Read("deployment", Created()));
        var settled = ReadDeployment(_provider.Read("deployment_status", Status("success")));

        Assert.Equal(created.ExternalId, settled.ExternalId);

        // The first says it is on its way; the second is what settles it.
        Assert.Equal(DeploymentState.Running, created.State);
        Assert.Equal(DeploymentState.Succeeded, settled.State);
    }

    /// <summary>
    /// An environment is grouped by what it resembles and shown by what it is called.
    /// </summary>
    /// <remarks>
    /// The name is whatever somebody typed into a workflow file, so the mapping is a
    /// guess and both halves of it matter. Classifying is what lets a page say "in
    /// production" without a column per spelling; keeping the original is what stops
    /// a firm that deploys to prod-eu being told it deployed to production, which is
    /// a slightly untrue statement in the one place it is worst to be approximately
    /// right.
    ///
    /// The unrecognised case is deliberately Other rather than a harder guess. A row
    /// filed under Other costs somebody a moment's reading; a row guessed into
    /// Production is a screen claiming something is live when it is not, which is how
    /// a release comes to be skipped because everybody believed it had already gone.
    /// </remarks>
    [Theory]
    [InlineData("production", DeploymentEnvironment.Production)]
    [InlineData("prod-eu", DeploymentEnvironment.Production)]
    [InlineData("staging", DeploymentEnvironment.Staging)]
    [InlineData("pre-prod", DeploymentEnvironment.Staging)]
    [InlineData("uat", DeploymentEnvironment.Development)]
    [InlineData("qa", DeploymentEnvironment.Development)]
    [InlineData("client-demo-nairobi", DeploymentEnvironment.Other)]
    public void An_environment_is_classified_and_keeps_its_own_name(
        string name, DeploymentEnvironment expected)
    {
        Assert.Equal(expected, Deployment.Classify(name));

        var deployment = Deployment.Record(
            Guid.NewGuid(),
            externalId: DeploymentId.ToString(),
            environmentName: name,
            sha: Sha,
            at: DateTimeOffset.UtcNow);

        Assert.Equal(expected, deployment.Environment);
        Assert.Equal(name, deployment.EnvironmentName);
    }

    /// <summary>
    /// A commit hash is stored in one case, whatever case the host reported it in.
    /// </summary>
    /// <remarks>
    /// A build is joined to its commit by string equality against a case-sensitive
    /// index, so a host reporting A1B2C3 where the push reported a1b2c3 leaves every
    /// build on that repository attached to nothing. There is no error and nothing in
    /// a log: the build panel on the work item is simply empty, and what anybody
    /// concludes from an empty panel is that the feature does not work, not that two
    /// strings disagree about capitals.
    ///
    /// The normalising belongs to the domain and not to the adapter, which is why
    /// this runs the payload through the provider and then through Build.Record —
    /// there are four adapters and one aggregate, and doing it in the adapters would
    /// be four chances to forget.
    /// </remarks>
    [Fact]
    public void A_build_stores_its_commit_in_lower_case()
    {
        var upper = Sha.ToUpperInvariant();

        var report = ReadBuild(_provider.Read(
            "workflow_run", WorkflowRun("completed", "success", sha: upper)));

        // The adapter passes the host's own text through untouched.
        Assert.Equal(upper, report.Sha);

        var build = Build.Record(
            Guid.NewGuid(),
            report.ExternalId,
            report.Name,
            report.Sha,
            report.Branch,
            workItemId: null,
            report.Outcome,
            report.StartedAt,
            report.FinishedAt,
            report.Url);

        Assert.Equal(Sha, build.Sha);
    }

    private static BuildReport ReadBuild(GitEvent read) =>
        Assert.IsType<GitEvent.Built>(read).Report;

    private static DeploymentReport ReadDeployment(GitEvent read) =>
        Assert.IsType<GitEvent.Deployed>(read).Report;

    /// <summary>
    /// A workflow_run body, in the shape GitHub sends all three times.
    /// </summary>
    /// <remarks>
    /// One helper rather than a pasted body per case, because most of these tests
    /// turn on two payloads differing in a single field being read differently — and
    /// a test that pasted twenty lines twice would let that field disappear into the
    /// difference between them.
    /// </remarks>
    private static string WorkflowRun(
        string status, string? conclusion, int attempt = 1, string? sha = null)
    {
        var written = conclusion is null ? "null" : $"\"{conclusion}\"";

        return $$"""
            {
              "action": "{{status}}",
              "workflow_run": {
                "id": {{RunId}},
                "name": "CI",
                "head_branch": "feature/412-payment-api",
                "head_sha": "{{sha ?? Sha}}",
                "run_number": 318,
                "run_attempt": {{attempt}},
                "event": "push",
                "status": "{{status}}",
                "conclusion": {{written}},
                "workflow_id": 148271,
                "html_url": "https://github.com/jiranisokotech/erp/actions/runs/{{RunId}}",
                "run_started_at": "2026-09-22T14:05:02Z",
                "created_at": "2026-09-22T14:04:58Z",
                "updated_at": "2026-09-22T14:09:14Z"
              },
              "repository": { "full_name": "jiranisokotech/erp" }
            }
            """;
    }

    /// <summary>The deployment object, which both deployment events carry identically.</summary>
    /// <remarks>
    /// Shared here for the same reason the adapter shares its reader. If the two
    /// payloads in this file drifted apart, the test asserting that a status settles a
    /// deployment would be comparing two bodies GitHub never sends together.
    /// </remarks>
    private static string DeploymentBlock(string environment) => $$"""
        {
            "id": {{DeploymentId}},
            "sha": "{{Sha}}",
            "ref": "main",
            "task": "deploy",
            "environment": "{{environment}}",
            "creator": { "login": "meshtirop1" },
            "created_at": "2026-09-22T17:02:00Z",
            "updated_at": "2026-09-22T17:02:00Z",
            "url": "https://api.github.com/repos/jiranisokotech/erp/deployments/1847263910"
          }
        """;

    private static string Created(string environment = "production") => $$"""
        {
          "action": "created",
          "deployment": {{DeploymentBlock(environment)}},
          "repository": { "full_name": "jiranisokotech/erp" }
        }
        """;

    private static string Status(string state, string environment = "production") => $$"""
        {
          "action": "created",
          "deployment_status": {
            "id": 2947163820,
            "state": "{{state}}",
            "environment": "{{environment}}",
            "description": "",
            "creator": { "login": "github-actions[bot]" },
            "target_url": "https://github.com/jiranisokotech/erp/actions/runs/10923847561",
            "created_at": "2026-09-22T17:06:41Z",
            "updated_at": "2026-09-22T17:06:41Z"
          },
          "deployment": {{DeploymentBlock(environment)}},
          "repository": { "full_name": "jiranisokotech/erp" }
        }
        """;
}
