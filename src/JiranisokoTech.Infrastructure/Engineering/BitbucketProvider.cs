using System.Text.Json;
using JiranisokoTech.Application.Engineering;
using JiranisokoTech.Domain.Engineering;
using static JiranisokoTech.Infrastructure.Engineering.Payload;

namespace JiranisokoTech.Infrastructure.Engineering;

/// <summary>
/// Bitbucket's headers, signature and payload shape.
/// </summary>
/// <remarks>
/// Written from Atlassian's documented payloads rather than verified against a live
/// workspace, which <see cref="GitHubProvider"/> was.
///
/// Bitbucket signs the body with HMAC-SHA256 like GitHub, so the security story
/// here is the good one rather than GitLab's plaintext token.
///
/// Its payloads differ from GitHub's in one way that shapes this file: a push
/// carries a list of <em>changes</em>, each with its own branch and its own commits,
/// so one delivery can touch several branches. This system's Pushed event describes
/// one branch, so the changes are flattened onto the first branch that carried
/// commits. That is a real narrowing and it is deliberate — a commit is recorded
/// once by its hash whichever branch it arrived on, so nothing is lost except the
/// knowledge that a second branch moved in the same delivery, which nothing here
/// uses.
/// </remarks>
public sealed class BitbucketProvider : IGitProvider
{
    public GitProvider Provider => GitProvider.Bitbucket;

    public bool IsSigned(ReadOnlySpan<byte> body, string? signature, string secret) =>
        Hmac.Signed(body, signature, secret);

    public string? DeliveryIdIn(IReadOnlyDictionary<string, string> headers, string payload) =>
        headers.GetValueOrDefault("X-Request-UUID") ?? headers.GetValueOrDefault("X-Hook-UUID");

    public string? EventIn(IReadOnlyDictionary<string, string> headers, string payload) =>
        headers.GetValueOrDefault("X-Event-Key");

    /// <summary>
    /// The unnumbered `X-Hub-Signature`, which is not GitHub's header.
    /// </summary>
    /// <remarks>
    /// Bitbucket uses the name without the algorithm suffix while still sending a
    /// `sha256=` prefixed digest inside it. Reading GitHub's header name here would
    /// find nothing and refuse every delivery as unsigned.
    /// </remarks>
    public string? SignatureIn(IReadOnlyDictionary<string, string> headers) =>
        headers.GetValueOrDefault("X-Hub-Signature");

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
            "repo:push" => ReadPush(root),

            "pullrequest:created" => Change(root, PullRequestAction.Opened),
            "pullrequest:updated" => Change(root, PullRequestAction.Updated),

            // Bitbucket's own words: fulfilled means merged, rejected means
            // declined. Nothing about either name says so, which is exactly why
            // they are mapped here and not guessed at somewhere else.
            "pullrequest:fulfilled" => Change(root, PullRequestAction.Merged),
            "pullrequest:rejected" => Change(root, PullRequestAction.Closed),

            "pullrequest:approved" => Review(root, ReviewVerdict.Approved),
            "pullrequest:changes_request_created" => Review(root, ReviewVerdict.ChangesRequested),

            _ => new GitEvent.Uninteresting($"Nothing here acts on a '{eventName}' event."),
        };
    }

    private static GitEvent ReadPush(JsonElement root)
    {
        foreach (var change in Each(root, "push", "changes"))
        {
            // new is null on a branch deletion, and a deletion carries no commits
            // worth recording under a ref that no longer exists.
            if (Text(change, "new", "type") != "branch"
                || Text(change, "new", "name") is not { Length: > 0 } branch)
            {
                continue;
            }

            var carried = Each(change, "commits")
                .Select(commit => new PushedCommit(
                    Text(commit, "hash") ?? string.Empty,
                    FirstLine(Text(commit, "message")),
                    Text(commit, "author", "user", "nickname")
                        ?? Text(commit, "author", "raw")
                        ?? "unknown",
                    When(commit, "date")))
                .Where(commit => commit.Sha.Length > 0)
                .ToList();

            if (carried.Count > 0)
            {
                return new GitEvent.Pushed(branch, carried);
            }
        }

        return new GitEvent.Uninteresting("A push carrying no commits on any branch.");
    }

    private static GitEvent Change(JsonElement root, PullRequestAction action)
    {
        if (!root.TryGetProperty("pullrequest", out var pull))
        {
            return new GitEvent.Uninteresting("A pull request event with no pull request on it.");
        }

        return new GitEvent.PullRequestChanged(new PullRequestChange(
            action,
            Number(pull, "id"),
            Text(pull, "title") ?? "(untitled)",
            Text(pull, "source", "branch", "name") ?? "(unknown)",
            Text(pull, "author", "nickname") ?? Text(pull, "author", "display_name") ?? "unknown",
            action == PullRequestAction.Opened
                ? When(pull, "created_on")
                : When(pull, "updated_on")));
    }

    /// <summary>
    /// An approval, keyed on the reviewer.
    /// </summary>
    /// <remarks>
    /// Bitbucket gives an approval no identifier of its own, so the pull request
    /// and the reviewer are the key — which makes a redelivery idempotent and
    /// treats an approval withdrawn and given again as the same approval. That is
    /// right: the latest word from a reviewer is what counts.
    /// </remarks>
    private static GitEvent Review(JsonElement root, ReviewVerdict verdict)
    {
        if (!root.TryGetProperty("pullrequest", out var pull))
        {
            return new GitEvent.Uninteresting("An approval with no pull request on it.");
        }

        var number = Number(pull, "id");

        var reviewer = Text(root, "approval", "user", "nickname")
            ?? Text(root, "actor", "nickname")
            ?? "unknown";

        return new GitEvent.Reviewed(
            number, $"bitbucket:{number}:{reviewer}:{verdict}", reviewer, verdict);
    }
}
