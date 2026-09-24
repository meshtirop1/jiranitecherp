using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Work;

/// <summary>
/// One word somebody put on a piece of work.
/// </summary>
/// <remarks>
/// Section 11's labels, as rows rather than a comma-separated column, because the question asked
/// of a label is "show me everything tagged frontend" and that is an index on a table or a
/// <c>LIKE '%frontend%'</c> that also matches "frontend-build".
///
/// <b>Reduced on the way in, and stored reduced.</b> "Frontend", "frontend" and " FrontEnd " are
/// one label to everybody except a string comparison, and a board where those are three separate
/// tags is a board where filtering by one of them hides two thirds of the work. The reduction is
/// <see cref="Slug"/>'s, which is already what this codebase uses for a short stable name.
/// </remarks>
public sealed class WorkItemLabel : Entity
{
    private WorkItemLabel() => Text = string.Empty;

    internal WorkItemLabel(string text) => Text = Slug.From(text).Value;

    public string Text { get; private init; }
}

/// <summary>
/// Something somebody said about a piece of work.
/// </summary>
/// <remarks>
/// <b>Not deletable, and editable only by whoever wrote it.</b> A comment thread somebody can
/// tidy is one nobody can rely on — the awkward question is always the one that disappears — and
/// an edit is marked so that a reply which no longer makes sense can be explained rather than
/// looking like nonsense.
/// </remarks>
public sealed class WorkItemComment : Entity
{
    private WorkItemComment() => Body = string.Empty;

    internal WorkItemComment(Guid byEmployeeId, string body, DateTimeOffset at)
    {
        ByEmployeeId = byEmployeeId;
        Body = Require(body);
        At = at;
    }

    public Guid ByEmployeeId { get; private init; }

    public string Body { get; private set; }

    public DateTimeOffset At { get; private init; }

    public DateTimeOffset? EditedAt { get; private set; }

    public bool WasEdited => EditedAt is not null;

    internal void Reword(Guid byEmployeeId, string body, DateTimeOffset at)
    {
        if (byEmployeeId != ByEmployeeId)
        {
            throw new InvalidOperationException(
                "A comment is edited by whoever wrote it. Reply instead — a thread where one "
                + "person can rewrite another's words is not a record of anything.");
        }

        Body = Require(body);
        EditedAt = at;
    }

    private static string Require(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ArgumentException("A comment has to say something.", nameof(body));
        }

        var trimmed = body.Trim();

        return trimmed.Length > 10_000
            ? throw new ArgumentException(
                "A comment cannot be longer than 10,000 characters.", nameof(body))
            : trimmed;
    }
}

/// <summary>
/// One thing that has to be true before a piece of work is done.
/// </summary>
/// <remarks>
/// Section 11 asks for checklists and acceptance criteria separately. They are the same
/// structure — a line of text and whether it is true yet — and building both produces two
/// half-used lists with the real criteria spread across them.
///
/// A line can be struck out rather than only ticked, for the one that turns out not to apply.
/// Without that, somebody facing a criterion that is no longer relevant either ticks it — which
/// makes the tick mean nothing — or deletes it, which loses the fact that it was once expected.
/// </remarks>
public sealed class DoneWhen : Entity
{
    private DoneWhen() => Text = string.Empty;

    internal DoneWhen(string text, int order)
    {
        Text = Require(text);
        Order = order;
    }

    public string Text { get; private set; }

    /// <summary>
    /// Where it sits in the list.
    /// </summary>
    /// <remarks>
    /// Kept explicitly, because a list of criteria has an order that means something and a
    /// collection's order coming back from a database is whatever the database felt like.
    /// </remarks>
    public int Order { get; private init; }

    public DateTimeOffset? MetAt { get; private set; }

    public Guid? MetById { get; private set; }

    /// <summary>Why it no longer applies. Null for a line that is still expected.</summary>
    public string? DroppedBecause { get; private set; }

    /// <summary>True when it has been ticked, or struck out for a reason.</summary>
    public bool IsMet => MetAt is not null || DroppedBecause is not null;

    public bool IsTicked => MetAt is not null;

    internal void Met(Guid byEmployeeId, DateTimeOffset at)
    {
        DroppedBecause = null;
        MetAt = at;
        MetById = byEmployeeId;
    }

    internal void NotMet()
    {
        MetAt = null;
        MetById = null;
        DroppedBecause = null;
    }

    internal void Drop(string because)
    {
        MetAt = null;
        MetById = null;
        DroppedBecause = string.IsNullOrWhiteSpace(because)
            ? throw new ArgumentException(
                "Say why it no longer applies. A criterion struck out with no reason is one "
                + "nobody can tell from one that was forgotten.", nameof(because))
            : because.Trim();
    }

    internal void Reword(string text) => Text = Require(text);

    private static string Require(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("This cannot be blank.", nameof(text));
        }

        var trimmed = text.Trim();

        return trimmed.Length > 500
            ? throw new ArgumentException(
                "A line of this list cannot be longer than 500 characters. Anything longer is "
                + "the description, not a criterion.", nameof(text))
            : trimmed;
    }
}

/// <summary>
/// One piece of work that has to happen before another can.
/// </summary>
/// <remarks>
/// Section 11's dependencies. Its own table rather than a collection on either side, because a
/// dependency belongs to neither item: it is a fact about the pair, and hanging it off one of
/// them makes deleting that one silently rewrite the other's plan.
///
/// <b>Direction matters and is easy to get backwards</b>, so the names are the sentence rather
/// than the jargon: <see cref="BlockerId"/> has to finish before <see cref="BlockedId"/> can.
/// </remarks>
public sealed class WorkItemLink : Entity
{
    private WorkItemLink()
    {
    }

    private WorkItemLink(Guid blockerId, Guid blockedId, DateTimeOffset at)
    {
        if (blockerId == blockedId)
        {
            throw new ArgumentException(
                "A piece of work cannot block itself.", nameof(blockedId));
        }

        BlockerId = blockerId;
        BlockedId = blockedId;
        At = at;
    }

    public static WorkItemLink Blocks(Guid blockerId, Guid blockedId, DateTimeOffset at) =>
        new(blockerId, blockedId, at);

    /// <summary>The one that has to finish first.</summary>
    public Guid BlockerId { get; private init; }

    /// <summary>The one that is waiting.</summary>
    public Guid BlockedId { get; private init; }

    public DateTimeOffset At { get; private init; }
}
