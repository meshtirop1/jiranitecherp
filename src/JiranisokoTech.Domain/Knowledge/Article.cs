using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Knowledge;

/// <summary>Where an article is in its life.</summary>
public enum ArticleState
{
    /// <summary>Being written. Only somebody who may write can see it.</summary>
    Draft = 1,

    /// <summary>Up, and answerable for.</summary>
    Published = 2,

    /// <summary>
    /// Taken down because it stopped being true.
    /// </summary>
    /// <remarks>
    /// Not deleted, and the difference is the point. Somebody is going to search for the thing
    /// this described and find nothing, and "we used to do it this way, and here is why we
    /// stopped" is a better answer than silence.
    /// </remarks>
    Retired = 3,
}

/// <summary>
/// A version of an article that somebody stood behind.
/// </summary>
/// <remarks>
/// Written on publication and on nothing else, which is the decision this type exists to
/// express. A revision per save is a table that grows with keystrokes and holds mostly
/// half-finished sentences; a revision per publication is the short list of versions the firm
/// actually told people were true, which is the list somebody wants when the question is what
/// this said in March.
/// </remarks>
public sealed class ArticleRevision : Entity
{
    private ArticleRevision() => Body = string.Empty;

    internal ArticleRevision(string body, Guid byEmployeeId, DateTimeOffset at, string? note)
    {
        Body = body;
        ByEmployeeId = byEmployeeId;
        At = at;
        Note = note;
    }

    public string Body { get; private init; }

    public Guid ByEmployeeId { get; private init; }

    public DateTimeOffset At { get; private init; }

    /// <summary>What changed, in the publisher's words. Optional.</summary>
    public string? Note { get; private init; }
}

/// <summary>
/// Something the firm knows, written down where the next person can find it.
/// </summary>
/// <remarks>
/// Section 25.
///
/// <b>The failure mode of a knowledge base is not an empty one. It is a full one nobody
/// trusts.</b> An article that was true when it was written and has quietly stopped being true
/// is worse than no article at all: somebody follows it, it does not work, and from then on
/// they check with a person instead — which is the state the knowledge base existed to fix,
/// arrived at by a longer route. So the weight of this design is not on editing, formatting or
/// filing. It is on <see cref="ReviewBy"/>: every published article names a day by which
/// somebody has to say out loud that it is still true, and one past that day is stale on the
/// screen rather than merely old.
///
/// <b>An owner, and never a team.</b> Review is an act somebody performs, and a date owed by
/// "Delivery" is a date owed by nobody.
///
/// <b>Labels, not a folder tree.</b> The brief draws a tree — Engineering, Architecture, APIs,
/// Deployment, Runbooks — and a tree makes filing a decision that is wrong about half the time,
/// because the article on deploying the payments service belongs under deployment and under
/// APIs both. Labels do not force the choice, the search box is how anybody actually arrives,
/// and the brief's own headings work perfectly well as labels. Spelled the way
/// <c>Attachment.Tags</c> is, so there is one idea of a label in this system rather than two.
///
/// <b>No per-article permissions.</b> Everybody who works here may read the whole of it. A
/// knowledge base with locked pages is one nobody can trust to be complete, so people stop
/// treating it as the answer and ask a person — again the failure this exists to prevent.
/// Anything that genuinely must be restricted is a document, and documents already carry a
/// permission and an access log.
/// </remarks>
public sealed class Article : Entity, IAuditable
{
    /// <summary>The longest an article may be.</summary>
    /// <remarks>
    /// Generous, because the alternative to a long article is a short article and a corridor
    /// conversation. Bounded at all because the body is copied into every revision, so an
    /// unbounded column is an unbounded table.
    /// </remarks>
    public const int LongestBody = 40_000;

    /// <summary>Two years, in days.</summary>
    private const int LongestPromise = 730;

    private readonly List<ArticleRevision> _revisions = [];

    private Article()
    {
        Key = string.Empty;
        Title = string.Empty;
        Summary = string.Empty;
        Body = string.Empty;
    }

    private Article(
        Slug key,
        string title,
        string summary,
        string body,
        Guid ownerId,
        string? labels,
        DateTimeOffset at)
    {
        Key = key.Value;
        Title = Text(title, nameof(title), 200);
        Summary = Text(summary, nameof(summary), 500);
        Body = Text(body, nameof(body), LongestBody);
        OwnerId = ownerId;
        Labels = Tidy(labels);
        WrittenAt = at;
        State = ArticleState.Draft;
    }

    /// <summary>Start one.</summary>
    /// <remarks>
    /// The key is handed in rather than derived here, and it is the one piece of this aggregate
    /// that needed somewhere else to decide. A slug is made from the title — nobody types one,
    /// because a typed slug is a mistyped slug and it ends up in an address people paste into
    /// chat — but two articles called "Deploying" is an ordinary thing for a firm to have, so
    /// something that can see the other articles has to settle which address this one gets.
    /// </remarks>
    public static Article Start(
        Slug key,
        string title,
        string summary,
        string body,
        Guid ownerId,
        DateTimeOffset at,
        string? labels = null) =>
        new(key, title, summary, body, ownerId, labels, at);

    /// <summary>The stable part of the address. Never changes once it exists.</summary>
    public string Key { get; private init; }

    public string Title { get; private set; }

    /// <summary>
    /// One or two sentences saying what is in it.
    /// </summary>
    /// <remarks>
    /// Required, and typed rather than taken from the first line of the body. This is what the
    /// list and the search results show, and a first line cut at eighty characters produces a
    /// page of half-sentences nobody can choose between — which turns a search into opening six
    /// tabs.
    /// </remarks>
    public string Summary { get; private set; }

    public string Body { get; private set; }

    /// <summary>Whose job it is to keep this true.</summary>
    public Guid OwnerId { get; private set; }

    /// <summary>Free text, comma separated, the way a document's tags are.</summary>
    public string? Labels { get; private set; }

    public ArticleState State { get; private set; }

    public DateTimeOffset WrittenAt { get; private init; }

    public DateTimeOffset? PublishedAt { get; private set; }

    /// <summary>
    /// When somebody last said this is still true.
    /// </summary>
    /// <remarks>
    /// Distinct from when it was last edited, and the distinction is the whole feature. An
    /// article can be correct and untouched for two years; an article can be edited weekly by
    /// somebody fixing typos and be wrong in its first paragraph the entire time. Only a person
    /// saying "I have read this and it is still right" moves this.
    /// </remarks>
    public DateTimeOffset? LastCheckedAt { get; private set; }

    /// <summary>Who last said so. A confirmation nobody signed is a date, not an answer.</summary>
    public Guid? LastCheckedById { get; private set; }

    /// <summary>The day by which somebody has to say it is still true.</summary>
    public DateOnly? ReviewBy { get; private set; }

    /// <summary>Why it was taken down, and what to read instead.</summary>
    public string? RetiredBecause { get; private set; }

    public DateTimeOffset? RetiredAt { get; private set; }

    public IReadOnlyList<ArticleRevision> Revisions =>
        [.. _revisions.OrderByDescending(one => one.At).ThenByDescending(one => one.Id)];

    public bool IsPublished => State == ArticleState.Published;

    /// <summary>Past the day somebody promised to check it.</summary>
    public bool IsStaleOn(DateOnly today) =>
        State == ArticleState.Published && ReviewBy is { } due && due < today;

    /// <summary>Change what it says.</summary>
    /// <remarks>
    /// Allowed in any state but retired, and it writes no revision — see
    /// <see cref="ArticleRevision"/>. Editing a published article leaves it published, because
    /// the alternative is that correcting one wrong sentence takes the right ones off the
    /// screen while somebody waits to press publish again.
    /// </remarks>
    public void Rewrite(string title, string summary, string body, string? labels)
    {
        Editable();

        Title = Text(title, nameof(title), 200);
        Summary = Text(summary, nameof(summary), 500);
        Body = Text(body, nameof(body), LongestBody);
        Labels = Tidy(labels);
    }

    public void Owner(Guid ownerId)
    {
        Editable();

        OwnerId = ownerId;
    }

    /// <summary>
    /// Put it up, and promise to check it by a date.
    /// </summary>
    /// <remarks>
    /// The date is required, refused in the past, and refused more than two years out. The
    /// second refusal is the one that matters: a five-year promise is not a promise, it is a
    /// way of making the box go away.
    /// </remarks>
    public void Publish(
        Guid byEmployeeId, DateOnly reviewBy, DateTimeOffset at, string? note = null)
    {
        if (State == ArticleState.Retired)
        {
            throw new InvalidOperationException(
                $"'{Title}' was retired. Start a new article rather than reviving this one — "
                + "anybody who read it while it was up read what it said then, and the note "
                + "saying why it came down is the useful half of a retired article.");
        }

        var today = DateOnly.FromDateTime(at.UtcDateTime);

        if (reviewBy < today)
        {
            throw new InvalidOperationException(
                $"A review date of {reviewBy:d MMMM yyyy} has already passed, so this article "
                + "would be stale the moment it went up. Name a day somebody will actually "
                + "look at it.");
        }

        if (reviewBy.DayNumber - today.DayNumber > LongestPromise)
        {
            throw new InvalidOperationException(
                "Two years is the longest anybody can promise to remember something. "
                + $"{reviewBy:d MMMM yyyy} is further off than that, and a date nobody will "
                + "reach is a way of clearing this box rather than an answer to it.");
        }

        _revisions.Add(new ArticleRevision(Body, byEmployeeId, at, note));

        State = ArticleState.Published;
        PublishedAt ??= at;
        LastCheckedAt = at;
        LastCheckedById = byEmployeeId;
        ReviewBy = reviewBy;
    }

    /// <summary>
    /// Somebody has read it and says it is still true.
    /// </summary>
    /// <remarks>
    /// The cheap act this whole design is arranged around, and it writes no revision because
    /// nothing changed. Kept separate from publishing on purpose: if confirming an article
    /// required editing it, nobody would confirm anything, and the review date would come to
    /// measure how recently somebody fixed a typo.
    /// </remarks>
    public void StillTrue(Guid byEmployeeId, DateOnly reviewBy, DateTimeOffset at)
    {
        if (State != ArticleState.Published)
        {
            throw new InvalidOperationException(
                $"'{Title}' is not up, so there is nothing to confirm. Publish it.");
        }

        if (reviewBy < DateOnly.FromDateTime(at.UtcDateTime))
        {
            throw new InvalidOperationException(
                $"A review date of {reviewBy:d MMMM yyyy} has already passed. Confirming an "
                + "article and leaving it stale in the same moment says nothing.");
        }

        LastCheckedAt = at;
        LastCheckedById = byEmployeeId;
        ReviewBy = reviewBy;
    }

    /// <summary>
    /// Take it down, and say why.
    /// </summary>
    /// <remarks>
    /// The reason is required. Somebody will search for what this described and find it
    /// retired, and a retired article with no explanation is indistinguishable from one taken
    /// down by accident — so they read it anyway and act on something the firm has stopped
    /// doing.
    /// </remarks>
    public void Retire(string because, DateTimeOffset at)
    {
        if (State == ArticleState.Retired)
        {
            throw new InvalidOperationException($"'{Title}' is already retired.");
        }

        RetiredBecause = Text(because, nameof(because), 1_000);
        RetiredAt = at;
        State = ArticleState.Retired;

        /*
         * Cleared, because a retired article cannot be stale — there is nothing left to be
         * wrong about. Leaving the date would put every article the firm has ever taken down
         * on the list of things somebody owes a review, which is how that list stops being
         * read.
         */
        ReviewBy = null;
    }

    /// <summary>The labels, split into the things somebody actually typed.</summary>
    public IReadOnlyList<string> Labelled() =>
        Labels is null
            ? []
            : [.. Labels.Split(
                ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private void Editable()
    {
        if (State == ArticleState.Retired)
        {
            throw new InvalidOperationException(
                $"'{Title}' was retired, and editing it would change what people are told the "
                + "firm used to do. Start a new article.");
        }
    }

    /// <summary>
    /// Labels, with the ways people type them evened out.
    /// </summary>
    /// <remarks>
    /// Trimmed, lower-cased and de-duplicated. Without this, "Deployment", " deployment" and
    /// "deployment " are three labels on one screen, which is how a label list becomes useless
    /// inside a month.
    /// </remarks>
    private static string? Tidy(string? labels)
    {
        if (string.IsNullOrWhiteSpace(labels))
        {
            return null;
        }

        var kept = labels
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(one => one.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToList();

        return kept.Count == 0 ? null : string.Join(", ", kept);
    }

    private static string Text(string value, string parameter, int longest)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("This cannot be blank.", parameter);
        }

        var trimmed = value.Trim();

        return trimmed.Length > longest
            ? throw new ArgumentException(
                $"This cannot be longer than {longest} characters.", parameter)
            : trimmed;
    }
}
