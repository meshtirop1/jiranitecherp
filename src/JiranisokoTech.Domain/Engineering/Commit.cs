using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Engineering;

/// <summary>
/// A commit, as the repository reported it.
/// </summary>
/// <remarks>
/// A mirror like <see cref="PullRequest"/>, and the most granular evidence this
/// system has that work actually happened. It exists so that a task can show
/// what was done against it without anybody writing a status update, which is
/// the whole point of the brief's first principle.
///
/// Deliberately thin. There is no diff, no file list and no line counts, and
/// that is a decision rather than an omission: storing them would mean holding
/// a second copy of the repository in a database that is not built for it, and
/// every question they would answer is better answered by following the link to
/// the provider. What is kept is what makes a commit findable and attributable.
/// </remarks>
public sealed class Commit : Entity, IAuditable
{
    private Commit()
    {
        Sha = string.Empty;
        Message = string.Empty;
        Author = string.Empty;
        Branch = string.Empty;
    }

    private Commit(
        Guid repositoryId,
        string sha,
        string message,
        string author,
        string branch,
        Guid? workItemId,
        DateTimeOffset at)
    {
        RepositoryId = repositoryId;
        Sha = Required(sha, nameof(sha));
        Message = Required(message, nameof(message));
        Author = Required(author, nameof(author));
        Branch = Required(branch, nameof(branch));
        WorkItemId = workItemId;
        At = at;

        Raise(new CommitRecorded(Id, repositoryId, Sha, workItemId, at));
    }

    public static Commit Record(
        Guid repositoryId,
        string sha,
        string message,
        string author,
        string branch,
        Guid? workItemId,
        DateTimeOffset at) =>
        new(repositoryId, sha, message, author, branch, workItemId, at);

    public Guid RepositoryId { get; private init; }

    /// <summary>
    /// The full hash, which is what makes a redelivery harmless.
    /// </summary>
    /// <remarks>
    /// Full rather than shortened. A short hash is what a person reads and it
    /// collides; this column is a uniqueness constraint, and two different
    /// commits landing on one row because seven characters matched would be
    /// silent and permanent.
    /// </remarks>
    public string Sha { get; private init; }

    /// <summary>The first line of the message, which is the part anybody reads.</summary>
    public string Message { get; private init; }

    public string Author { get; private init; }

    public string Branch { get; private init; }

    /// <summary>The work this belongs to, if the branch or message said.</summary>
    public Guid? WorkItemId { get; private set; }

    public DateTimeOffset At { get; private init; }

    /// <summary>The short hash, for reading.</summary>
    public string Short => Sha.Length > 7 ? Sha[..7] : Sha;

    public void Belongs(Guid? workItemId) => WorkItemId = workItemId;

    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private static string Required(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();
}

public sealed record CommitRecorded(
    Guid CommitId,
    Guid RepositoryId,
    string Sha,
    Guid? WorkItemId,
    DateTimeOffset At) : DomainEvent;
