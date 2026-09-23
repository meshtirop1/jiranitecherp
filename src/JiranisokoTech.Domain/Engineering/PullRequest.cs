using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Engineering;

public enum PullRequestState
{
    Open = 1,
    Merged = 2,

    /// <summary>Closed without merging. Not the same thing as merged.</summary>
    Closed = 3,
}

/// <summary>What a reviewer said.</summary>
public enum ReviewVerdict
{
    Approved = 1,
    ChangesRequested = 2,

    /// <summary>Read it, said something, did not block it.</summary>
    Commented = 3,
}

/// <summary>
/// A pull request, as the provider reports it.
/// </summary>
/// <remarks>
/// This is a mirror, not a source. The repository is where a pull request
/// actually lives; this record exists so that the rest of the system can see
/// one without anybody typing it in — so a task can show that it has a pull
/// request open, a reviewer can be shown to have asked for changes, and the
/// chain the brief describes can be followed from a piece of work to what
/// shipped.
///
/// Because it is a mirror, every write here is an update from a delivery rather
/// than a decision somebody made. It carries no rules about what a pull request
/// may do — the provider already decided that — and the only thing this
/// enforces is that the mirror cannot go backwards: a merge reported twice is
/// still one merge, and a stale delivery arriving late cannot un-merge
/// something.
/// </remarks>
public sealed class PullRequest : Entity, IAuditable
{
    private readonly List<Review> _reviews = [];

    private PullRequest()
    {
        Title = string.Empty;
        Branch = string.Empty;
        Author = string.Empty;
    }

    private PullRequest(
        Guid repositoryId,
        int number,
        string title,
        string branch,
        string author,
        Guid? workItemId,
        DateTimeOffset openedAt)
    {
        RepositoryId = repositoryId;
        Number = number;
        Title = Required(title, nameof(title));
        Branch = Required(branch, nameof(branch));
        Author = Required(author, nameof(author));
        WorkItemId = workItemId;
        State = PullRequestState.Open;
        OpenedAt = openedAt;

        Raise(new PullRequestOpened(Id, repositoryId, number, Title, workItemId, openedAt));
    }

    public static PullRequest Opened(
        Guid repositoryId,
        int number,
        string title,
        string branch,
        string author,
        Guid? workItemId,
        DateTimeOffset openedAt) =>
        new(repositoryId, number, title, branch, author, workItemId, openedAt);

    public Guid RepositoryId { get; private init; }

    /// <summary>The number the provider gave it, which is what people say.</summary>
    public int Number { get; private init; }

    public string Title { get; private set; }

    public string Branch { get; private init; }

    /// <summary>The provider's login for whoever opened it.</summary>
    /// <remarks>
    /// Stored as the provider's name rather than resolved to an employee,
    /// because an outside contributor has no staff record and a resolution that
    /// fails is worse than a name that is simply not one of ours. Matching it to
    /// a person is a separate question with its own answer.
    /// </remarks>
    public string Author { get; private set; }

    /// <summary>The work this belongs to, if the branch or title said.</summary>
    public Guid? WorkItemId { get; private set; }

    public PullRequestState State { get; private set; }

    public DateTimeOffset OpenedAt { get; private init; }

    public DateTimeOffset? ClosedAt { get; private set; }

    /// <remarks>Returns a copy — see the note on Invoice.Lines for why.</remarks>
    public IReadOnlyList<Review> Reviews => _reviews.ToList();

    public bool IsOpen => State == PullRequestState.Open;

    /// <summary>Somebody has approved it and nobody is asking for changes.</summary>
    /// <remarks>
    /// The last word from each reviewer, not every word: a reviewer who asked
    /// for changes and then approved has approved, and counting both would
    /// leave a pull request permanently blocked by a comment it has already
    /// answered.
    /// </remarks>
    public bool IsApproved =>
        _reviews.Count > 0
        && Latest().Any(review => review.Verdict == ReviewVerdict.Approved)
        && Latest().All(review => review.Verdict != ReviewVerdict.ChangesRequested);

    public void Retitle(string title) => Title = Required(title, nameof(title));

    public void Belongs(Guid? workItemId) => WorkItemId = workItemId;

    /// <summary>
    /// Record what a reviewer said.
    /// </summary>
    /// <remarks>
    /// Keyed on the provider's own identifier for the review so that a delivery
    /// arriving twice — which is normal, because providers retry — does not
    /// produce two of them.
    /// </remarks>
    public void Reviewed(
        string externalId, string reviewer, ReviewVerdict verdict, DateTimeOffset at)
    {
        if (_reviews.Any(review => review.ExternalId == externalId))
        {
            return;
        }

        _reviews.Add(Review.Of(externalId, reviewer, verdict, at));

        Raise(new PullRequestReviewed(Id, RepositoryId, Number, reviewer, verdict, at));
    }

    /// <summary>
    /// It was merged.
    /// </summary>
    /// <remarks>
    /// Refuses to move a pull request that is already finished, in either
    /// direction. Providers redeliver, and they do not promise order: a stale
    /// "closed" arriving after a "merged" would otherwise rewrite history into
    /// something that never happened, and the board would act on it.
    /// </remarks>
    public void Merged(DateTimeOffset at)
    {
        if (State != PullRequestState.Open)
        {
            return;
        }

        State = PullRequestState.Merged;
        ClosedAt = at;

        Raise(new PullRequestMerged(Id, RepositoryId, Number, Branch, WorkItemId, at));
    }

    /// <summary>Closed without merging.</summary>
    public void Closed(DateTimeOffset at)
    {
        if (State != PullRequestState.Open)
        {
            return;
        }

        State = PullRequestState.Closed;
        ClosedAt = at;

        Raise(new PullRequestClosed(Id, RepositoryId, Number, WorkItemId, at));
    }

    private IEnumerable<Review> Latest() => _reviews
        .GroupBy(review => review.Reviewer)
        .Select(group => group.OrderByDescending(review => review.At).First());

    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private static string Required(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();
}

/// <summary>One reviewer's verdict, as the provider reported it.</summary>
public sealed class Review
{
    private Review()
    {
        ExternalId = string.Empty;
        Reviewer = string.Empty;
    }

    internal static Review Of(
        string externalId, string reviewer, ReviewVerdict verdict, DateTimeOffset at) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ExternalId = externalId,
            Reviewer = reviewer,
            Verdict = verdict,
            At = at,
        };

    public Guid Id { get; private init; }

    /// <summary>The provider's identifier, which is what makes this idempotent.</summary>
    public string ExternalId { get; private init; }

    public string Reviewer { get; private init; }

    public ReviewVerdict Verdict { get; private init; }

    public DateTimeOffset At { get; private init; }
}

public sealed record PullRequestOpened(
    Guid PullRequestId,
    Guid RepositoryId,
    int Number,
    string Title,
    Guid? WorkItemId,
    DateTimeOffset At) : DomainEvent;

public sealed record PullRequestReviewed(
    Guid PullRequestId,
    Guid RepositoryId,
    int Number,
    string Reviewer,
    ReviewVerdict Verdict,
    DateTimeOffset At) : DomainEvent;

/// <summary>
/// Merged, which is the event the rest of the system cares about.
/// </summary>
/// <remarks>
/// Its own event rather than a state change on a general one, because this is
/// what work being finished looks like from the outside — and the thing that
/// can move a task without anybody touching the board.
/// </remarks>
public sealed record PullRequestMerged(
    Guid PullRequestId,
    Guid RepositoryId,
    int Number,
    string Branch,
    Guid? WorkItemId,
    DateTimeOffset At) : DomainEvent;

public sealed record PullRequestClosed(
    Guid PullRequestId,
    Guid RepositoryId,
    int Number,
    Guid? WorkItemId,
    DateTimeOffset At) : DomainEvent;
