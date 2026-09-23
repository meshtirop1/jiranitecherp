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
}
