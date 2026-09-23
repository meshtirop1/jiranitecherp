using System.Text.Json;
using JiranisokoTech.Application.Engineering;
using JiranisokoTech.Domain.Engineering;
using static JiranisokoTech.Infrastructure.Engineering.Payload;

namespace JiranisokoTech.Infrastructure.Engineering;

/// <summary>
/// GitLab's headers, GitLab's token, GitLab's payload shape.
/// </summary>
/// <remarks>
/// Written from GitLab's documented payloads rather than verified against a live
/// instance, which <see cref="GitHubProvider"/> was. The difference is worth
/// stating: the tests here use bodies trimmed from GitLab's published examples, so
/// they prove this adapter reads what GitLab says it sends, not what a particular
/// GitLab version actually sent.
///
/// <b>The security difference matters more.</b> GitLab does not sign the body. It
/// sends the shared secret in plaintext in `X-Gitlab-Token` and expects the
/// receiver to compare it. That proves the caller knows the secret and nothing at
/// all about the payload — anything able to alter the request in flight can alter
/// what it says and the check still passes. GitHub's and Bitbucket's HMAC is
/// strictly better, and this is a reason to prefer either where there is a choice.
/// </remarks>
public sealed class GitLabProvider : IGitProvider
{
    public GitProvider Provider => GitProvider.GitLab;

    /// <summary>A plaintext token comparison, because that is all GitLab offers.</summary>
    public bool IsSigned(ReadOnlySpan<byte> body, string? signature, string secret) =>
        Hmac.SecretMatches(signature, secret);

    /// <summary>
    /// GitLab's event UUID.
    /// </summary>
    /// <remarks>
    /// Sent on webhook deliveries but not on every kind — a system hook may arrive
    /// without one. Falling back to a hash of the body would look helpful and be
    /// wrong: two genuine identical pushes would then collide and the second would
    /// be discarded as a duplicate. Returning nothing refuses the delivery as
    /// malformed instead, which is visible and correctable.
    /// </remarks>
    public string? DeliveryIdIn(IReadOnlyDictionary<string, string> headers, string payload) =>
        headers.GetValueOrDefault("X-Gitlab-Event-UUID");

    public string? EventIn(IReadOnlyDictionary<string, string> headers, string payload)
    {
        /*
         * The body's object_kind is preferred over the header. GitLab sends
         * "Merge Request Hook" in the header for opening, merging, closing and
         * approving alike, while object_kind plus the action inside distinguishes
         * them — and one name per distinct event is what the deliveries screen
         * needs to be readable.
         */
        try
        {
            using var document = JsonDocument.Parse(payload);

            if (Text(document.RootElement, "object_kind") is { Length: > 0 } kind)
            {
                return kind;
            }
        }
        catch (JsonException)
        {
            // Falls through to the header. A body that will not parse is still a
            // delivery worth recording, and the dispatcher will say so properly.
        }

        return headers.GetValueOrDefault("X-Gitlab-Event");
    }

    public string? SignatureIn(IReadOnlyDictionary<string, string> headers) =>
        headers.GetValueOrDefault("X-Gitlab-Token");

    public string? RepositoryIn(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        // path_with_namespace is GitLab's owner/name. The nested project is where
        // a merge request event keeps it; the flat one is where a push does.
        return Text(root, "project", "path_with_namespace")
            ?? Text(root, "repository", "name");
    }

    public GitEvent Read(string eventName, string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        return eventName switch
        {
            "push" => ReadPush(root),
            "merge_request" => ReadMergeRequest(root),
            "tag_push" => new GitEvent.Uninteresting("A tag push, not a branch push."),
            "pipeline" => ReadPipeline(root),
            "deployment" => ReadDeployment(root),

            /*
             * GitLab calls a single job "build", which is the most expensive name collision in
             * this file. A matrix pipeline of six jobs sends six of these, each with its own
             * identifier and all with the same commit — so each would be recorded as a
             * separate build and a work item would show seven builds of one commit, six of
             * them fragments of the seventh. The pipeline is the thing worth recording.
             */
            "build" => new GitEvent.Uninteresting(
                "A single job inside a pipeline; the pipeline itself is what is recorded."),
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
                // GitLab sends an email and a name on the commit author and no
                // username, so the name is the best that can be had here.
                Text(commit, "author", "name") ?? "unknown",
                When(commit, "timestamp")))
            .Where(commit => commit.Sha.Length > 0)
            .ToList();

        return carried.Count == 0
            ? new GitEvent.Uninteresting("A push carrying no commits, most likely a deletion.")
            : new GitEvent.Pushed(branch, carried);
    }

    /// <summary>
    /// A merge request event, which may also be an approval.
    /// </summary>
    /// <remarks>
    /// GitLab folds review into the same event as the state change: the action is
    /// "approved", "unapproved", "open", "merge" or "close". So one payload shape
    /// produces either a pull request change or a review, which is why this returns
    /// the union type rather than one of them.
    /// </remarks>
    private static GitEvent ReadMergeRequest(JsonElement root)
    {
        if (!root.TryGetProperty("object_attributes", out var merge))
        {
            return new GitEvent.Uninteresting(
                "A merge request event with no object_attributes on it.");
        }

        // iid, not id. The iid is the number people say and the one shown in the
        // interface; id is GitLab's internal row identifier and is meaningless to
        // anybody reading a board.
        var number = Number(merge, "iid");
        var action = Text(merge, "action");

        if (action is "approved" or "unapproved")
        {
            /*
             * GitLab gives an approval no identifier of its own, so the reviewer
             * and the merge request are used as the key. That makes a redelivered
             * approval idempotent, and it makes an approval that was withdrawn and
             * given again the same approval — which is right: the latest word from
             * a reviewer is what counts, and a second row saying the same thing
             * would not change the answer.
             */
            var reviewer = Text(root, "user", "username")
                ?? Text(root, "user", "name")
                ?? "unknown";

            return new GitEvent.Reviewed(
                number,
                $"gitlab:{number}:{reviewer}:{action}",
                reviewer,
                action == "approved" ? ReviewVerdict.Approved : ReviewVerdict.ChangesRequested);
        }

        var what = action switch
        {
            "open" or "reopen" => PullRequestAction.Opened,
            "update" => PullRequestAction.Updated,
            "merge" => PullRequestAction.Merged,
            "close" => PullRequestAction.Closed,
            _ => (PullRequestAction?)null,
        };

        if (what is not { } settled)
        {
            return new GitEvent.Uninteresting(
                $"Nothing here acts on a merge request being '{action}'.");
        }

        return new GitEvent.PullRequestChanged(new PullRequestChange(
            settled,
            number,
            Text(merge, "title") ?? "(untitled)",
            Text(merge, "source_branch") ?? "(unknown)",
            Text(root, "user", "username") ?? Text(root, "user", "name") ?? "unknown",
            settled == PullRequestAction.Opened
                ? When(merge, "created_at")
                : When(merge, "updated_at")));
    }

    /// <summary>
    /// A pipeline, reduced to whether it is going and how it went.
    /// </summary>
    /// <remarks>
    /// GitLab's statuses are created, waiting_for_resource, preparing, pending, running,
    /// success, failed, canceled, skipped and manual. Only five outcomes matter and the one
    /// worth naming is manual: a pipeline stopped at a gate waiting for a person, which is
    /// neither running nor finished. Reading it as either would be wrong in a way somebody
    /// acts on.
    /// </remarks>
    private static GitEvent ReadPipeline(JsonElement root)
    {
        if (Identifier(root, "object_attributes", "id") is not { Length: > 0 } externalId)
        {
            return new GitEvent.Uninteresting("A pipeline event with no identifier.");
        }

        var attributes = root.GetProperty("object_attributes");

        if (Text(attributes, "sha") is not { Length: > 0 } sha)
        {
            return new GitEvent.Uninteresting("A pipeline with no commit to attach it to.");
        }

        var status = Text(attributes, "status");

        var outcome = status switch
        {
            "success" => BuildOutcome.Passed,
            "canceled" or "cancelled" or "skipped" => BuildOutcome.Cancelled,
            "manual" or "waiting_for_resource" => BuildOutcome.Blocked,
            "created" or "preparing" or "pending" or "running" => BuildOutcome.Running,

            /*
             * Anything unrecognised is a failure rather than a pass, for the same reason
             * GitHub's reader treats it so: a status GitLab adds later must never be read as
             * green, because a build reporting success when it did not is the one fault in
             * this file that costs somebody a release.
             */
            _ => BuildOutcome.Failed,
        };

        var startedAt = When(attributes, "created_at");

        return new GitEvent.Built(new BuildReport(
            externalId,
            Text(attributes, "name") is { Length: > 0 } name ? name : "(unnamed pipeline)",
            sha,
            Text(attributes, "ref") is { Length: > 0 } reference ? reference : "(no branch)",
            outcome,
            startedAt,
            outcome is BuildOutcome.Running or BuildOutcome.Blocked
                ? null
                : When(attributes, "finished_at"),
            Text(root, "object_attributes", "url")));
    }

    /// <summary>What reached an environment, as GitLab reports it.</summary>
    /// <remarks>
    /// GitLab's deployment statuses are running, success, failed and canceled. A cancelled
    /// deployment is read as failed rather than given a state of its own: unlike a build,
    /// where cancelling says nothing about the code, a release that was called off part way
    /// through has left an environment in a state somebody has to look at.
    /// </remarks>
    private static GitEvent ReadDeployment(JsonElement root)
    {
        if (Identifier(root, "deployment_id") is not { Length: > 0 } externalId)
        {
            return new GitEvent.Uninteresting("A deployment event with no identifier.");
        }

        if (Text(root, "environment") is not { Length: > 0 } environment
            || Text(root, "commit_url") is null && Text(root, "short_sha") is null)
        {
            return new GitEvent.Uninteresting(
                "A deployment with no environment or no commit to attach it to.");
        }

        /*
         * GitLab sends short_sha and a commit_url rather than the full hash. The short hash
         * would not match the commits table, whose Sha column is the full one and uniquely
         * indexed, so the full hash is taken out of the end of the url where it is present.
         * Falling back to the short one keeps the row rather than dropping it, and an
         * unattached deployment is better than none.
         */
        var sha = Text(root, "commit_url") is { } url && url.LastIndexOf('/') is var cut and > 0
            ? url[(cut + 1)..]
            : Text(root, "short_sha") ?? string.Empty;

        if (sha.Length == 0)
        {
            return new GitEvent.Uninteresting("A deployment with no commit to attach it to.");
        }

        var status = Text(root, "status");

        return new GitEvent.Deployed(new DeploymentReport(
            externalId,
            environment,
            sha,
            Text(root, "ref"),
            Text(root, "user", "username"),
            status switch
            {
                "success" => DeploymentState.Succeeded,
                "failed" or "canceled" or "cancelled" => DeploymentState.Failed,
                _ => DeploymentState.Running,
            },
            When(root, "status_changed_at"),
            Text(root, "deployable_url")));
    }
}
