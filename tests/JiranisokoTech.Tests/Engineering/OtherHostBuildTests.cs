using JiranisokoTech.Application.Engineering;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Infrastructure.Engineering;

namespace JiranisokoTech.Tests.Engineering;

/// <summary>
/// Builds and deployments from the three hosts that are not GitHub.
/// </summary>
/// <remarks>
/// The checklist has said for weeks that only GitHub is verified against real deliveries and
/// the other three are written from published payloads. That is still true of the shapes here
/// — these are trimmed but structurally real bodies — and it is why the adapters are tested
/// this precisely: a field at the wrong path is a whole host silently recording nothing, and
/// the symptom is an empty panel that looks exactly like a firm that has not set up CI.
/// </remarks>
public class OtherHostBuildTests
{
    /// <summary>
    /// GitLab's "manual" is neither running nor finished.
    /// </summary>
    /// <remarks>
    /// The outcome that earned its own enum member. A pipeline stopped at a gate read as
    /// running shows as building for three days; read as failed it puts a red mark against
    /// code nobody has found fault with. The two want different reactions, and only one of
    /// them is "press the button".
    /// </remarks>
    [Fact]
    public void A_gitlab_pipeline_waiting_at_a_gate_is_neither_running_nor_finished()
    {
        var read = new GitLabProvider().Read("pipeline", $$"""
            {
              "object_attributes": {
                "id": 31415926535,
                "sha": "9f2a1c4e8b7d6a5f3e2c1b0a9d8e7f6a5b4c3d2e",
                "ref": "main",
                "name": "Deploy",
                "status": "manual",
                "created_at": "2026-09-24 08:00:00 UTC",
                "url": "https://gitlab.test/acme/erp/-/pipelines/31415926535"
              }
            }
            """);

        var built = Assert.IsType<GitEvent.Built>(read);

        Assert.Equal(BuildOutcome.Blocked, built.Report.Outcome);
        Assert.Null(built.Report.FinishedAt);
        Assert.Equal("Deploy", built.Report.Name);
    }

    /// <summary>
    /// A GitLab job is not a pipeline, and recording it would multiply every build.
    /// </summary>
    /// <remarks>
    /// The most expensive name collision in these adapters. GitLab calls a single job
    /// "build", and a matrix pipeline of six jobs sends six of them — each with its own
    /// identifier and all with the same commit. Reading them would put seven builds of one
    /// commit on a work item, six of them fragments of the seventh.
    /// </remarks>
    [Fact]
    public void A_gitlab_job_is_not_recorded_as_a_build()
    {
        var read = new GitLabProvider().Read("build", """{"build_id": 7}""");

        var why = Assert.IsType<GitEvent.Uninteresting>(read);

        Assert.Contains("pipeline", why.Why);
    }

    /// <summary>
    /// A status GitLab has not thought of yet is a failure, never a pass.
    /// </summary>
    /// <remarks>
    /// The same rule as the GitHub reader, and for the same reason: a build reporting green
    /// when it is not is the one fault in these files that costs somebody a release.
    /// </remarks>
    [Fact]
    public void An_unrecognised_gitlab_status_is_read_as_a_failure()
    {
        var read = new GitLabProvider().Read("pipeline", $$"""
            {
              "object_attributes": {
                "id": 2,
                "sha": "abc1234def5678",
                "ref": "main",
                "status": "something_gitlab_added_last_tuesday"
              }
            }
            """);

        Assert.Equal(BuildOutcome.Failed, Assert.IsType<GitEvent.Built>(read).Report.Outcome);
    }

    /// <summary>
    /// A Bitbucket status is keyed by its pipeline AND its commit.
    /// </summary>
    /// <remarks>
    /// A status key is the pipeline's name and is reused on every commit it ever runs
    /// against. Keying on it alone would give one row that every build in the repository
    /// overwrote, so a work item would show a single perpetually-changing build instead of a
    /// history — and nothing would look wrong.
    /// </remarks>
    [Fact]
    public void A_bitbucket_status_is_keyed_by_pipeline_and_commit()
    {
        var provider = new BitbucketProvider();

        var first = Assert.IsType<GitEvent.Built>(
            provider.Read("repo:commit_status_updated", Status("aaa111", "SUCCESSFUL")));

        var second = Assert.IsType<GitEvent.Built>(
            provider.Read("repo:commit_status_updated", Status("bbb222", "SUCCESSFUL")));

        Assert.NotEqual(first.Report.ExternalId, second.Report.ExternalId);
        Assert.Contains("aaa111", first.Report.ExternalId);
    }

    [Fact]
    public void A_bitbucket_status_carries_its_outcome()
    {
        var provider = new BitbucketProvider();

        Assert.Equal(
            BuildOutcome.Passed,
            Assert.IsType<GitEvent.Built>(
                provider.Read("repo:commit_status_updated", Status("aaa111", "SUCCESSFUL")))
                .Report.Outcome);

        Assert.Equal(
            BuildOutcome.Running,
            Assert.IsType<GitEvent.Built>(
                provider.Read("repo:commit_status_updated", Status("aaa111", "INPROGRESS")))
                .Report.Outcome);

        Assert.Equal(
            BuildOutcome.Failed,
            Assert.IsType<GitEvent.Built>(
                provider.Read("repo:commit_status_updated", Status("aaa111", "FAILED")))
                .Report.Outcome);
    }

    /// <summary>
    /// An Azure run identifier is read as text, not as an int.
    /// </summary>
    /// <remarks>
    /// The fault that would have dead-lettered every build delivery on GitHub, checked here
    /// too because the same helper is the only thing standing between this adapter and it.
    /// </remarks>
    [Fact]
    public void An_azure_build_is_read_with_its_definition_and_commit()
    {
        var read = new AzureDevOpsProvider().Read("build.complete", $$"""
            {
              "resource": {
                "id": 10923847561,
                "definition": { "name": "ERP-CI" },
                "sourceVersion": "9F2A1C4E8B7D6A5F3E2C1B0A9D8E7F6A5B4C3D2E",
                "sourceBranch": "refs/heads/release/2026-09",
                "result": "succeeded",
                "startTime": "2026-09-24T08:00:00Z",
                "finishTime": "2026-09-24T08:04:12Z"
              }
            }
            """);

        var built = Assert.IsType<GitEvent.Built>(read);

        Assert.Equal("10923847561", built.Report.ExternalId);
        Assert.Equal("ERP-CI", built.Report.Name);
        Assert.Equal("release/2026-09", built.Report.Branch);
        Assert.Equal(BuildOutcome.Passed, built.Report.Outcome);
    }

    /// <summary>
    /// An Azure release is declined, and says so rather than recording nothing.
    /// </summary>
    /// <remarks>
    /// Its payload does not carry the commit at any stable path — it is inside
    /// resource.deployment.release.artifacts, and which key holds it depends on whether the
    /// artifact is a build, a Git ref or a package. A deployment needs the sha, because that
    /// is what joins a release to the work it came from. Declining out loud beats a reader
    /// that works on one firm's pipeline and silently records nothing on another's.
    /// </remarks>
    [Fact]
    public void An_azure_release_is_declined_with_a_reason()
    {
        var read = new AzureDevOpsProvider()
            .Read("ms.vss-release.deployment-completed-event", """{"resource": {}}""");

        Assert.Contains("commit", Assert.IsType<GitEvent.Uninteresting>(read).Why);
    }

    /// <summary>
    /// A commit hash is stored lower-cased whichever host sent it.
    /// </summary>
    /// <remarks>
    /// Azure sends upper-case hex. The build-to-commit join is string equality against a
    /// case-sensitive unique index, so without this every build from that host would attach to
    /// nothing — an empty panel, no error, and nothing in a log to look at.
    /// </remarks>
    [Fact]
    public void A_hash_is_stored_lower_cased_whichever_host_sent_it()
    {
        var built = Build.Record(
            Guid.CreateVersion7(),
            "1",
            "ERP-CI",
            "9F2A1C4E8B7D6A5F3E2C1B0A9D8E7F6A5B4C3D2E",
            "main",
            null,
            BuildOutcome.Passed,
            DateTimeOffset.UtcNow);

        Assert.Equal("9f2a1c4e8b7d6a5f3e2c1b0a9d8e7f6a5b4c3d2e", built.Sha);
    }

    /// <summary>
    /// A blocked build can still be settled when somebody opens the gate.
    /// </summary>
    /// <remarks>
    /// The reason Ended guards on "settled" rather than on "running". Guarding on running
    /// would refuse the delivery that arrives when the button is finally pressed, leaving the
    /// build reading "waiting on somebody" for ever with nothing coming to correct it.
    /// </remarks>
    [Fact]
    public void A_blocked_build_can_still_be_settled()
    {
        var built = Build.Record(
            Guid.CreateVersion7(), "1", "Deploy", "abc123", "main", null,
            BuildOutcome.Blocked, DateTimeOffset.UtcNow);

        Assert.Null(built.FinishedAt);

        built.Ended(BuildOutcome.Passed, DateTimeOffset.UtcNow);

        Assert.Equal(BuildOutcome.Passed, built.Outcome);
        Assert.NotNull(built.FinishedAt);
    }

    private static string Status(string sha, string state) => $$"""
        {
          "commit_status": {
            "key": "PIPELINE",
            "name": "Pipeline",
            "state": "{{state}}",
            "refname": "main",
            "url": "https://bitbucket.test/acme/erp/builds/1",
            "created_on": "2026-09-24T08:00:00Z",
            "updated_on": "2026-09-24T08:04:00Z",
            "commit": { "hash": "{{sha}}" }
          }
        }
        """;
}
