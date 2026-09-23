using System.Text.Json;
using JiranisokoTech.Application.Engineering;
using JiranisokoTech.Domain.Engineering;
using static JiranisokoTech.Infrastructure.Engineering.Payload;

namespace JiranisokoTech.Infrastructure.Engineering;

/// <summary>
/// GitHub's headers, GitHub's signature scheme, GitHub's payload shape.
/// </summary>
/// <remarks>
/// The reference adapter, and the only one of the four verified against real
/// deliveries. The others were written from the providers' documentation and
/// their tests use payloads trimmed from published examples, which is not the
/// same thing and is said plainly in each.
///
/// The parsing is deliberately shallow: it reads the handful of fields this
/// system uses and ignores the rest of a payload that runs to several hundred
/// lines, so that GitHub adding a field — which they do — cannot break anything
/// here. A field missing where it was needed raises, the delivery is marked
/// failed, and the body is on disk to try again with once this is corrected.
/// </remarks>
public sealed class GitHubProvider : IGitProvider
{
    public GitProvider Provider => GitProvider.GitHub;

    public bool IsSigned(ReadOnlySpan<byte> body, string? signature, string secret) =>
        Hmac.Signed(body, signature, secret);

    public string? DeliveryIdIn(IReadOnlyDictionary<string, string> headers, string payload) =>
        headers.GetValueOrDefault("X-GitHub-Delivery");

    public string? EventIn(IReadOnlyDictionary<string, string> headers, string payload) =>
        headers.GetValueOrDefault("X-GitHub-Event");

    public string? SignatureIn(IReadOnlyDictionary<string, string> headers) =>
        headers.GetValueOrDefault("X-Hub-Signature-256");

    public string? RepositoryIn(string payload)
    {
        using var document = JsonDocument.Parse(payload);

        return Text(document.RootElement, "repository", "full_name");
    }

    public GitEvent Read(string eventName, string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        return eventName switch
        {
            "push" => ReadPush(root),
            "pull_request" => ReadPullRequest(root),
            "pull_request_review" => ReadReview(root),
            "workflow_run" => ReadBuild(root),
            "deployment" => ReadDeploymentCreated(root),
            "deployment_status" => ReadDeploymentStatus(root),

            /*
             * Named, and deliberately not read.
             *
             * GitHub sends check_suite and check_run for the same Actions run that
             * workflow_run describes, with different identifiers. Reading any second one
             * of them would record the same build twice under two keys, and a task would
             * show "CI passed" beside "CI passed" for one run — with no way for anybody
             * to tell which was the duplicate. workflow_run is the one that carries the
             * workflow's name, which is the field that makes a build worth reporting.
             *
             * A repository using a third-party CI that reports through the Checks API and
             * not through Actions will record nothing here. That is a real gap and it is
             * better than a silent double count; it is written down in the checklist.
             */
            "check_suite" or "check_run" => new GitEvent.Uninteresting(
                "A checks event for a run that workflow_run already reports."),

            /*
             * Named rather than lumped in with the noise, because "ping" means the
             * webhook was just configured and is the single most reassuring thing
             * anybody setting one up can see on the deliveries screen.
             */
            "ping" => new GitEvent.Uninteresting(
                "GitHub checking the webhook. The connection works."),

            _ => new GitEvent.Uninteresting($"Nothing here acts on a '{eventName}' event."),
        };
    }

    private static GitEvent ReadPush(JsonElement root)
    {
        if (Branch(Text(root, "ref")) is not { } branch)
        {
            return new GitEvent.Uninteresting("Not a push to a branch.");
        }

        var carried = Each(root, "commits")
            .Select(commit => new PushedCommit(
                Text(commit, "id") ?? string.Empty,
                FirstLine(Text(commit, "message")),
                Text(commit, "author", "username") ?? Text(commit, "author", "name") ?? "unknown",
                When(commit, "timestamp")))
            .Where(commit => commit.Sha.Length > 0)
            .ToList();

        return carried.Count == 0
            ? new GitEvent.Uninteresting("A push carrying no commits, most likely a deletion.")
            : new GitEvent.Pushed(branch, carried);
    }

    /// <summary>
    /// A pull request event, reduced to what happened to it.
    /// </summary>
    /// <remarks>
    /// GitHub says "closed" for both a merge and an abandonment, and the only
    /// thing separating them is a boolean further down the payload. Reading that
    /// boolean is the difference between a board that shows shipped work and one
    /// that shows every branch anybody ever gave up on as though it had shipped.
    /// </remarks>
    private static GitEvent ReadPullRequest(JsonElement root)
    {
        if (!root.TryGetProperty("pull_request", out var pull))
        {
            return new GitEvent.Uninteresting("A pull request event with no pull request on it.");
        }

        var action = Text(root, "action");
        var merged = Flag(pull, "merged");

        var what = action switch
        {
            "opened" or "reopened" => PullRequestAction.Opened,
            "edited" or "synchronize" or "ready_for_review" => PullRequestAction.Updated,
            "closed" when merged => PullRequestAction.Merged,
            "closed" => PullRequestAction.Closed,
            _ => (PullRequestAction?)null,
        };

        if (what is not { } settled)
        {
            return new GitEvent.Uninteresting(
                $"Nothing here acts on a pull request being '{action}'.");
        }

        return new GitEvent.PullRequestChanged(new PullRequestChange(
            settled,
            Number(pull, "number"),
            Text(pull, "title") ?? "(untitled)",
            Text(pull, "head", "ref") ?? "(unknown)",
            Text(pull, "user", "login") ?? "unknown",
            settled switch
            {
                PullRequestAction.Merged => When(pull, "merged_at"),
                PullRequestAction.Closed => When(pull, "closed_at"),
                PullRequestAction.Opened => When(pull, "created_at"),
                _ => When(pull, "updated_at"),
            }));
    }

    private static GitEvent ReadReview(JsonElement root)
    {
        if (Text(root, "action") != "submitted"
            || !root.TryGetProperty("review", out var review))
        {
            return new GitEvent.Uninteresting("Not a review being submitted.");
        }

        var verdict = Text(review, "state")?.ToLowerInvariant() switch
        {
            "approved" => ReviewVerdict.Approved,
            "changes_requested" => ReviewVerdict.ChangesRequested,
            "commented" => ReviewVerdict.Commented,
            _ => (ReviewVerdict?)null,
        };

        if (verdict is not { } settled)
        {
            return new GitEvent.Uninteresting("A review in a state nothing here acts on.");
        }

        return new GitEvent.Reviewed(
            Number(root, "pull_request", "number"),
            Number(review, "id").ToString(),
            Text(review, "user", "login") ?? "unknown",
            settled);
    }

    /// <summary>
    /// A CI run, reduced to whether it is going and how it went.
    /// </summary>
    /// <remarks>
    /// One reader for the whole life of a run. GitHub sends workflow_run three times —
    /// requested, in_progress, completed — all with the same identifier and the same body
    /// shape, so the record is created by whichever arrives first and settled by the one
    /// that carries a conclusion.
    ///
    /// The status is read before the conclusion, in that order, because a completed run
    /// carries both and an in-progress one carries a conclusion of null. Reading the
    /// conclusion first and treating null as a failure is the obvious mistake, and it
    /// would put a red mark on every build for the fifteen minutes it was running.
    /// </remarks>
    private static GitEvent ReadBuild(JsonElement root)
    {
        if (Identifier(root, "workflow_run", "id") is not { Length: > 0 } runId)
        {
            return new GitEvent.Uninteresting("A workflow event with no run identifier.");
        }

        var externalId = runId;

        var run = root.GetProperty("workflow_run");

        /*
         * The attempt is part of the identity, and leaving it out is a fault that took a
         * second reading to see. Pressing "re-run" in GitHub keeps the same run id and
         * increments run_attempt — so a run keyed on the id alone would match the settled
         * row from the first attempt, be dropped as a stale redelivery, and leave the work
         * item page showing a failure that had since been fixed. Nothing would appear in a
         * log, because dropping a stale redelivery is the correct behaviour it was imitating.
         *
         * A re-run is a different build of the same commit, and this makes it one.
         */
        var attempt = Identifier(run, "run_attempt") ?? "1";
        externalId = $"{externalId}-{attempt}";

        if (Text(run, "head_sha") is not { Length: > 0 } sha)
        {
            return new GitEvent.Uninteresting("A workflow run with no commit to attach it to.");
        }

        /*
         * The branch can be absent on a run triggered by a schedule or by hand against a
         * tag. Recorded as the head branch's name when there is one, and otherwise as the
         * word for what it is — because Build requires a branch, and a blank there would be
         * a column somebody filters on holding an empty string.
         */
        var branch = Text(run, "head_branch") is { Length: > 0 } named ? named : "(no branch)";

        var status = Text(run, "status");
        var conclusion = Text(run, "conclusion");

        var outcome = status switch
        {
            "completed" => conclusion switch
            {
                "success" => BuildOutcome.Passed,
                "cancelled" or "skipped" or "stale" => BuildOutcome.Cancelled,

                /*
                 * Everything else that is not a success is a failure, including
                 * timed_out, action_required and neutral. Listing the failure words
                 * instead would mean a conclusion GitHub adds later is silently read as
                 * a pass, and a build that reports green when it is not is the one fault
                 * in this file that would cost somebody a release.
                 */
                _ => BuildOutcome.Failed,
            },
            _ => BuildOutcome.Running,
        };

        var startedAt = When(run, "run_started_at");

        return new GitEvent.Built(new BuildReport(
            externalId,
            Text(run, "name") is { Length: > 0 } name ? name : "(unnamed workflow)",
            sha,
            branch,
            outcome,
            startedAt,
            outcome == BuildOutcome.Running ? null : When(run, "updated_at"),
            Text(run, "html_url")));
    }

    /// <summary>
    /// A deployment was created, before anything has been said about how it went.
    /// </summary>
    /// <remarks>
    /// Read as well as deployment_status, and the two share an identifier so the second
    /// settles the first rather than adding a row. Reading only the status events would be
    /// tempting — they carry everything — but GitHub does not guarantee one, and a
    /// deployment that nothing ever reported on would simply not exist here. A row saying
    /// "going out, nothing heard since" is the more useful of the two silences.
    /// </remarks>
    private static GitEvent ReadDeploymentCreated(JsonElement root)
    {
        if (ReadDeploymentBasics(root) is not { } basics)
        {
            return new GitEvent.Uninteresting(
                "A deployment event without an identifier, an environment or a commit.");
        }

        return new GitEvent.Deployed(basics with
        {
            State = DeploymentState.Running,
            At = When(root, "deployment", "created_at"),
        });
    }

    /// <summary>What actually happened to a deployment.</summary>
    /// <remarks>
    /// GitHub's states are pending, queued, in_progress, success, failure, error and
    /// inactive. Only three of those are worth a column, and inactive is the interesting
    /// one to get right: it means a previous deployment was superseded, not that anything
    /// went wrong, so it is read as uninteresting rather than as a failure — otherwise
    /// every successful release would be followed by a red mark against the one before it.
    /// </remarks>
    private static GitEvent ReadDeploymentStatus(JsonElement root)
    {
        if (ReadDeploymentBasics(root) is not { } basics)
        {
            return new GitEvent.Uninteresting(
                "A deployment status without an identifier, an environment or a commit.");
        }

        var state = Text(root, "deployment_status", "state");

        if (state == "inactive")
        {
            return new GitEvent.Uninteresting(
                "A deployment marked inactive because a later one superseded it.");
        }

        return new GitEvent.Deployed(basics with
        {
            State = state switch
            {
                "success" => DeploymentState.Succeeded,
                "failure" or "error" => DeploymentState.Failed,
                _ => DeploymentState.Running,
            },
            At = When(root, "deployment_status", "updated_at"),
            Url = Text(root, "deployment_status", "target_url")
                ?? Text(root, "deployment", "url"),
        });
    }

    /// <summary>
    /// The half of a deployment payload that both events carry identically.
    /// </summary>
    /// <remarks>
    /// Shared so the two readers cannot disagree about which identifier keys a
    /// deployment. If one of them used the deployment's id and the other the status's,
    /// every release would appear twice and the pair would never be reconciled.
    /// </remarks>
    private static DeploymentReport? ReadDeploymentBasics(JsonElement root)
    {
        if (Identifier(root, "deployment", "id") is not { Length: > 0 } externalId)
        {
            return null;
        }

        var deployment = root.GetProperty("deployment");

        if (Text(deployment, "environment") is not { Length: > 0 } environment
            || Text(deployment, "sha") is not { Length: > 0 } sha)
        {
            return null;
        }

        return new DeploymentReport(
            externalId,
            environment,
            sha,
            Branch(Text(deployment, "ref")) ?? Text(deployment, "ref"),
            Text(deployment, "creator", "login"),
            DeploymentState.Running,
            When(deployment, "created_at"),
            Text(deployment, "url"));
    }
}
