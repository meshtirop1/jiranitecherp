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
}
