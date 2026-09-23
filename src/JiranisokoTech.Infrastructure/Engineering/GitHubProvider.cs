using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JiranisokoTech.Application.Engineering;
using JiranisokoTech.Domain.Engineering;

namespace JiranisokoTech.Infrastructure.Engineering;

/// <summary>
/// GitHub's headers, GitHub's signature scheme, GitHub's payload shape.
/// </summary>
/// <remarks>
/// Everything provider-specific about the integration is in this one file, which
/// is the point of the adapter: adding GitLab means writing a second one of
/// these and nothing else.
///
/// The parsing is deliberately defensive and deliberately shallow. It reads the
/// handful of fields the system uses and ignores the rest of a payload that runs
/// to several hundred lines, so that GitHub adding a field — which they do —
/// cannot break anything here. A field that is missing where it was expected
/// produces an exception, the delivery is marked failed, and the body is on disk
/// to try again with once the adapter has been corrected.
/// </remarks>
public sealed class GitHubProvider : IGitProvider
{
    private const string DeliveryHeader = "X-GitHub-Delivery";
    private const string EventHeader = "X-GitHub-Event";
    private const string SignatureHeader = "X-Hub-Signature-256";
    private const string SignaturePrefix = "sha256=";

    public GitProvider Provider => GitProvider.GitHub;

    /// <summary>
    /// HMAC-SHA256 over the exact bytes, compared in constant time.
    /// </summary>
    /// <remarks>
    /// Two details here are load-bearing and both are easy to get wrong.
    ///
    /// The comparison is <see cref="CryptographicOperations.FixedTimeEquals"/>
    /// rather than string equality. An ordinary comparison returns as soon as
    /// two bytes differ, and the time it takes leaks how much of the signature
    /// was right — which is enough to recover a valid signature byte by byte
    /// against an endpoint that can be called as often as this one can.
    ///
    /// The hash is computed over the raw request bytes. Anything that decodes
    /// the body to a string first and re-encodes it is computing the signature
    /// of a different sequence of bytes whenever the payload contains a
    /// character that does not round-trip, and the failure looks precisely like
    /// a forged request.
    /// </remarks>
    public bool IsSigned(ReadOnlySpan<byte> body, string? signature, string secret)
    {
        if (signature is null || !signature.StartsWith(SignaturePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var hex = signature.AsSpan(SignaturePrefix.Length);

        if (hex.Length != 64)
        {
            return false;
        }

        /*
         * Parsed by hand rather than with Convert.FromHexString, which raises
         * on a stray character. A malformed signature is something any
         * unauthenticated caller can send at will, and an endpoint that throws
         * on request is one that anybody can fill the error log with.
         */
        Span<byte> offered = stackalloc byte[32];

        for (var index = 0; index < offered.Length; index++)
        {
            if (!byte.TryParse(
                    hex.Slice(index * 2, 2),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out var parsed))
            {
                return false;
            }

            offered[index] = parsed;
        }

        Span<byte> expected = stackalloc byte[32];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body, expected);

        return CryptographicOperations.FixedTimeEquals(expected, offered);
    }

    public string? DeliveryIdIn(IReadOnlyDictionary<string, string> headers) =>
        headers.GetValueOrDefault(DeliveryHeader);

    public string? EventIn(IReadOnlyDictionary<string, string> headers) =>
        headers.GetValueOrDefault(EventHeader);

    public string? SignatureIn(IReadOnlyDictionary<string, string> headers) =>
        headers.GetValueOrDefault(SignatureHeader);

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
             * Named rather than lumped together, because "ping" means the
             * webhook was just configured and is the single most reassuring
             * thing anybody setting one up can see on the deliveries screen.
             */
            "ping" => new GitEvent.Uninteresting(
                "GitHub checking the webhook. The connection works."),

            _ => new GitEvent.Uninteresting($"Nothing here acts on a '{eventName}' event."),
        };
    }

    /// <summary>
    /// A push, reduced to a branch and some commits.
    /// </summary>
    /// <remarks>
    /// Tag and branch-deletion pushes are discarded here. A deletion carries an
    /// empty commit list and a ref that no longer exists, and a tag push
    /// duplicates commits already recorded against the branch they were made
    /// on — recording either would put the same work in the history twice under
    /// a branch name nobody typed.
    /// </remarks>
    private static GitEvent ReadPush(JsonElement root)
    {
        var reference = Text(root, "ref");

        if (reference is null || !reference.StartsWith("refs/heads/", StringComparison.Ordinal))
        {
            return new GitEvent.Uninteresting("Not a push to a branch.");
        }

        var branch = reference["refs/heads/".Length..];

        if (!root.TryGetProperty("commits", out var commits)
            || commits.ValueKind != JsonValueKind.Array)
        {
            return new GitEvent.Uninteresting("A push carrying no commits.");
        }

        var carried = commits
            .EnumerateArray()
            .Select(commit => new PushedCommit(
                Text(commit, "id") ?? string.Empty,
                FirstLine(Text(commit, "message")),
                Text(commit, "author", "username")
                    ?? Text(commit, "author", "name")
                    ?? "unknown",
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
    /// that shows every branch anybody ever gave up on as though it had
    /// shipped.
    /// </remarks>
    private static GitEvent ReadPullRequest(JsonElement root)
    {
        if (!root.TryGetProperty("pull_request", out var pull))
        {
            return new GitEvent.Uninteresting("A pull request event with no pull request on it.");
        }

        var action = Text(root, "action");

        var merged = pull.TryGetProperty("merged", out var flag)
            && flag.ValueKind == JsonValueKind.True;

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

        var at = settled switch
        {
            PullRequestAction.Merged => When(pull, "merged_at"),
            PullRequestAction.Closed => When(pull, "closed_at"),
            PullRequestAction.Opened => When(pull, "created_at"),
            _ => When(pull, "updated_at"),
        };

        return new GitEvent.PullRequestChanged(new PullRequestChange(
            settled,
            Number(pull, "number"),
            Text(pull, "title") ?? "(untitled)",
            Text(pull, "head", "ref") ?? "(unknown)",
            Text(pull, "user", "login") ?? "unknown",
            at));
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
            Number(root.GetProperty("pull_request"), "number"),
            Number(review, "id").ToString(),
            Text(review, "user", "login") ?? "unknown",
            settled);
    }

    /// <summary>
    /// The first line of a commit message.
    /// </summary>
    /// <remarks>
    /// The rest is the body, which is where people paste stack traces, and a
    /// board column is not the place for one. The whole message is still in the
    /// stored payload, and in the repository, which is where anybody wanting it
    /// would look.
    /// </remarks>
    private static string FirstLine(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "(no message)";
        }

        var end = message.IndexOfAny(['\r', '\n']);
        var line = end < 0 ? message : message[..end];

        // Capped to the column it is stored in. Nothing enforces a subject
        // length on the far side, and a commit whose first line runs past a
        // thousand characters would otherwise fail to save and take every other
        // commit in the same delivery down with it.
        if (line.Length > 1000)
        {
            line = line[..1000];
        }

        return line.Length > 0 ? line : "(no message)";
    }

    private static string? Text(JsonElement element, params string[] path)
    {
        var current = element;

        foreach (var step in path)
        {
            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(step, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static int Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : throw new InvalidOperationException(
                $"The payload has no numeric '{name}', which this event cannot be recorded "
                + "without.");

    /// <summary>
    /// A timestamp from the payload, or now.
    /// </summary>
    /// <remarks>
    /// The provider's own time is preferred because it is when the thing
    /// actually happened, and a delivery replayed a week later would otherwise
    /// claim every commit in it was made the day somebody pressed the button.
    /// Falls back to now when the field is absent or unparseable, because a
    /// commit recorded with a slightly wrong timestamp is far better than one
    /// not recorded at all.
    /// </remarks>
    private static DateTimeOffset When(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), out var at)
            ? at
            : DateTimeOffset.UtcNow;
}
