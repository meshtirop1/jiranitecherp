using System.Text.Json;
using JiranisokoTech.Application.Engineering;
using JiranisokoTech.Domain.Engineering;
using static JiranisokoTech.Infrastructure.Engineering.Payload;

namespace JiranisokoTech.Infrastructure.Engineering;

/// <summary>
/// Azure DevOps service hooks.
/// </summary>
/// <remarks>
/// Written from Microsoft's documented payloads rather than verified against a live
/// organisation, which <see cref="GitHubProvider"/> was.
///
/// The odd one of the four, in two ways that both had to be accommodated rather
/// than worked around.
///
/// <b>It does not sign anything.</b> Azure DevOps service hooks offer basic
/// authentication on the receiving endpoint instead of an HMAC, so what arrives is
/// an `Authorization: Basic …` header and the check is a constant-time comparison
/// against the configured secret. Like GitLab's token that proves the caller knows
/// the secret and nothing about the body. It is what the provider offers.
///
/// <b>Its delivery identifier is in the body</b>, as a top-level `id`, where the
/// other three use a header. That is the reason `DeliveryIdIn` is given the payload
/// as well as the headers.
/// </remarks>
public sealed class AzureDevOpsProvider : IGitProvider
{
    public GitProvider Provider => GitProvider.AzureDevOps;

    /// <summary>
    /// The basic authorization header, compared whole.
    /// </summary>
    /// <remarks>
    /// Compared as sent rather than decoded into a user and a password, so the
    /// configured secret is the entire header value after "Basic ". Setting it is
    /// then one instruction — base64 of `user:password` — and there is no second
    /// interpretation of where the user ends and the password begins for somebody
    /// to get wrong.
    /// </remarks>
    public bool IsSigned(ReadOnlySpan<byte> body, string? signature, string secret) =>
        Hmac.SecretMatches(signature, secret);

    public string? DeliveryIdIn(IReadOnlyDictionary<string, string> headers, string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);

            return Text(document.RootElement, "id");
        }
        catch (JsonException)
        {
            // Refused as malformed rather than invented. A delivery with no
            // identifier cannot be made idempotent, and a substitute would break
            // the replay protection that identifier is carrying.
            return null;
        }
    }

    public string? EventIn(IReadOnlyDictionary<string, string> headers, string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);

            return Text(document.RootElement, "eventType");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The Authorization header, which is where the credential is.
    /// </summary>
    /// <remarks>
    /// The prefix is stripped so the configured value is the credential itself
    /// rather than the word "Basic" plus the credential. Returning the header whole
    /// would work equally well and would make the configuration instruction one
    /// word longer and one mistake easier.
    /// </remarks>
    public string? SignatureIn(IReadOnlyDictionary<string, string> headers) =>
        headers.GetValueOrDefault("Authorization") is { } offered
        && offered.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)
            ? offered["Basic ".Length..].Trim()
            : null;

    /// <summary>
    /// Project and repository, joined the way the other three name things.
    /// </summary>
    /// <remarks>
    /// Azure DevOps has no single owner/name string, so the project name and the
    /// repository name are joined with a slash. That is what somebody connecting
    /// one here has to type, and it is said on the repositories screen rather than
    /// left to be discovered by a delivery that matched nothing.
    /// </remarks>
    public string? RepositoryIn(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        var name = Text(root, "resource", "repository", "name")
            ?? Text(root, "resource", "repository", "project", "name");

        var project = Text(root, "resource", "repository", "project", "name")
            ?? Text(root, "resourceContainers", "project", "id");

        return name is null
            ? null
            : project is null ? name : $"{project}/{name}";
    }

    public GitEvent Read(string eventName, string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        return eventName switch
        {
            "git.push" => ReadPush(root),
            "git.pullrequest.created" => Change(root, PullRequestAction.Opened),
            "git.pullrequest.updated" => ReadUpdate(root),
            "git.pullrequest.merged" => Change(root, PullRequestAction.Merged),
            _ => new GitEvent.Uninteresting($"Nothing here acts on a '{eventName}' event."),
        };
    }

    private static GitEvent ReadPush(JsonElement root)
    {
        var reference = Each(root, "resource", "refUpdates")
            .Select(update => Text(update, "name"))
            .FirstOrDefault(name => Branch(name) is not null);

        if (Branch(reference) is not { } branch)
        {
            return new GitEvent.Uninteresting("Not a push to a branch.");
        }

        var carried = Each(root, "resource", "commits")
            .Select(commit => new PushedCommit(
                Text(commit, "commitId") ?? string.Empty,
                // "comment" is Azure DevOps' name for the commit message.
                FirstLine(Text(commit, "comment")),
                Text(commit, "author", "name") ?? "unknown",
                When(commit, "author", "date")))
            .Where(commit => commit.Sha.Length > 0)
            .ToList();

        return carried.Count == 0
            ? new GitEvent.Uninteresting("A push carrying no commits.")
            : new GitEvent.Pushed(branch, carried);
    }

    /// <summary>
    /// An update, which is also how Azure DevOps reports an abandonment.
    /// </summary>
    /// <remarks>
    /// There is no `git.pullrequest.abandoned` event. A pull request that somebody
    /// gives up on arrives as an update whose status is "abandoned", and reading
    /// that status is the difference between a board showing abandoned work as
    /// still open and showing it as closed.
    /// </remarks>
    private static GitEvent ReadUpdate(JsonElement root) =>
        Text(root, "resource", "status") == "abandoned"
            ? Change(root, PullRequestAction.Closed)
            : Change(root, PullRequestAction.Updated);

    private static GitEvent Change(JsonElement root, PullRequestAction action)
    {
        if (!root.TryGetProperty("resource", out var pull))
        {
            return new GitEvent.Uninteresting("A pull request event with no resource on it.");
        }

        return new GitEvent.PullRequestChanged(new PullRequestChange(
            action,
            Number(pull, "pullRequestId"),
            Text(pull, "title") ?? "(untitled)",
            Branch(Text(pull, "sourceRefName")) ?? "(unknown)",
            // uniqueName is the sign-in address; displayName is the person's name.
            // The address is preferred because it is stable and unique, and a
            // display name is neither.
            Text(pull, "createdBy", "uniqueName")
                ?? Text(pull, "createdBy", "displayName")
                ?? "unknown",
            action switch
            {
                PullRequestAction.Opened => When(pull, "creationDate"),
                PullRequestAction.Merged or PullRequestAction.Closed => When(pull, "closedDate"),
                _ => DateTimeOffset.UtcNow,
            }));
    }
}
