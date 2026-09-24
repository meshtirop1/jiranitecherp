using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Performance;

/// <summary>
/// What a review concluded.
/// </summary>
/// <remarks>
/// Three bands, not five. A five-point scale sounds more precise and is not: nobody can say what
/// separates a three from a four, so the number ends up being argued about instead of the work,
/// and everybody lands on the middle. Three bands force a sentence to carry the detail, which is
/// where the detail belongs.
///
/// Nullable, too. A firm of this size does not need a label on everybody to have the conversation,
/// and a review with notes on both sides and no band is a complete review.
/// </remarks>
public enum ReviewRating
{
    Below = 1,
    Meeting = 2,
    Exceeding = 3,
}

/// <summary>
/// One person's review in one cycle.
/// </summary>
/// <remarks>
/// Two halves that are written separately and deliberately not symmetrical.
///
/// <b>Nobody writes somebody else's half.</b> The person writes theirs and their manager writes
/// theirs, and neither can touch the other's — a self-assessment a manager can edit is not a
/// self-assessment, it is a form somebody filled in on your behalf.
///
/// <b>The manager's half is invisible until it is shared.</b> This is the rule that matters most
/// and the one a page would forget, so it is enforced here rather than left to a screen: reading a
/// half-written appraisal of yourself is the specific harm this feature could do, and it does not
/// take malice — a manager saving a draft on Friday to finish on Monday is enough.
/// </remarks>
public sealed class Review : Entity
{
    private Review()
    {
    }

    internal Review(Guid employeeId, Guid? managerEmployeeId)
    {
        EmployeeId = employeeId;
        ManagerEmployeeId = managerEmployeeId;
    }

    public Guid EmployeeId { get; private init; }

    /// <summary>
    /// Who is writing the other half.
    /// </summary>
    /// <remarks>
    /// Taken from the reporting line when the cycle is opened and then kept, because somebody who
    /// changes manager in November is still being reviewed for the year by whoever worked with
    /// them — and a value that followed the org chart would reassign a half-written review to
    /// somebody who has never met them.
    /// </remarks>
    public Guid? ManagerEmployeeId { get; private set; }

    public string? SelfNote { get; private set; }

    public DateTimeOffset? SelfWrittenAt { get; private set; }

    private string? ManagerNote { get; set; }

    public DateTimeOffset? ManagerWrittenAt { get; private set; }

    public ReviewRating? Rating { get; private set; }

    /// <summary>When the manager's half was shown to the person. Null while it is private.</summary>
    public DateTimeOffset? SharedAt { get; private set; }

    public bool IsShared => SharedAt is not null;

    public bool HasSelfNote => !string.IsNullOrWhiteSpace(SelfNote);

    public bool HasManagerNote => !string.IsNullOrWhiteSpace(ManagerNote);

    /// <summary>
    /// The manager's half, if this reader is allowed it.
    /// </summary>
    /// <remarks>
    /// The one accessor, so that no page can reach the text another way. The subject gets it once
    /// it is shared and not before; anybody else who has got as far as this object has been
    /// through the permission check on the way in.
    ///
    /// Returning null rather than throwing, because the caller is a screen deciding what to draw
    /// and "nothing to show yet" is an ordinary state rather than an error.
    /// </remarks>
    public string? ManagerNoteFor(Guid readerId) =>
        readerId == EmployeeId && !IsShared ? null : ManagerNote;

    /// <summary>The rating, under the same rule as the note it belongs with.</summary>
    public ReviewRating? RatingFor(Guid readerId) =>
        readerId == EmployeeId && !IsShared ? null : Rating;

    internal void WriteSelf(Guid byEmployeeId, string note, DateTimeOffset at)
    {
        if (byEmployeeId != EmployeeId)
        {
            throw new InvalidOperationException(
                "A self-assessment is written by the person it is about. Write the manager's "
                + "half instead.");
        }

        if (IsShared)
        {
            throw new InvalidOperationException(
                "This review has been shared and the conversation has happened. Anything to add "
                + "belongs in the next cycle, or in a goal.");
        }

        SelfNote = Trim(note, nameof(note));
        SelfWrittenAt = at;
    }

    internal void WriteManager(
        Guid byEmployeeId, string note, ReviewRating? rating, DateTimeOffset at)
    {
        if (byEmployeeId == EmployeeId)
        {
            throw new InvalidOperationException(
                "Nobody writes the manager's half of their own review.");
        }

        ManagerNote = Trim(note, nameof(note));
        Rating = rating;
        ManagerWrittenAt = at;
    }

    internal void Share(DateTimeOffset at)
    {
        if (!HasManagerNote)
        {
            throw new InvalidOperationException(
                "There is nothing to share yet. Write the manager's half first — sharing an "
                + "empty review tells somebody their appraisal is ready and shows them nothing.");
        }

        SharedAt ??= at;
    }

    internal void Manager(Guid? employeeId) => ManagerEmployeeId = employeeId;

    private static string Trim(string note, string parameter)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            throw new ArgumentException("This cannot be blank.", parameter);
        }

        var trimmed = note.Trim();

        return trimmed.Length > 10_000
            ? throw new ArgumentException(
                "This cannot be longer than 10,000 characters.", parameter)
            : trimmed;
    }
}

/// <summary>
/// A round of reviews, covering a period.
/// </summary>
/// <remarks>
/// Section 6. The cycle exists so that a review has a period and an end: without one, "have you
/// done your reviews" is a question nobody can answer, and the answer matters because an
/// unfinished review is somebody who has written about their year and been told nothing back.
///
/// Opening a cycle creates a review for everybody it covers, rather than leaving them to appear
/// when somebody starts writing. A list that fills up as people participate cannot show who has
/// not, which is the only thing anybody wants from it.
/// </remarks>
public sealed class ReviewCycle : Entity, IAuditable
{
    private readonly List<Review> _reviews = [];

    private ReviewCycle() => Name = string.Empty;

    private ReviewCycle(string name, DateOnly from, DateOnly to)
    {
        if (to < from)
        {
            throw new ArgumentException("A cycle cannot end before it starts.", nameof(to));
        }

        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("A cycle needs a name.", nameof(name))
            : name.Trim();
        From = from;
        To = to;
    }

    public static ReviewCycle Open(string name, DateOnly from, DateOnly to) =>
        new(name, from, to);

    /// <summary>What it is called. "2026, mid-year" rather than a number.</summary>
    public string Name { get; private set; }

    /// <summary>The period being reviewed, which is not when the reviewing happens.</summary>
    public DateOnly From { get; private init; }

    public DateOnly To { get; private init; }

    public bool IsClosed { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public IReadOnlyList<Review> Reviews =>
        [.. _reviews.OrderBy(one => one.EmployeeId)];

    public Review? For(Guid employeeId) =>
        _reviews.FirstOrDefault(one => one.EmployeeId == employeeId);

    /// <summary>Reviews where the manager has written nothing, which is the chase list.</summary>
    public IReadOnlyList<Review> Awaiting =>
        [.. _reviews.Where(one => !one.HasManagerNote)];

    public IReadOnlyList<Review> NotShared =>
        [.. _reviews.Where(one => one.HasManagerNote && !one.IsShared)];

    /// <summary>
    /// Put somebody in the cycle.
    /// </summary>
    /// <remarks>
    /// Idempotent, because opening a cycle over the staff list and then adding a late joiner are
    /// the same call, and a second row for one person would give them two reviews with no way to
    /// say which is theirs.
    /// </remarks>
    public Review Include(Guid employeeId, Guid? managerEmployeeId)
    {
        Refuse();

        if (For(employeeId) is { } already)
        {
            already.Manager(managerEmployeeId);

            return already;
        }

        var review = new Review(employeeId, managerEmployeeId);

        _reviews.Add(review);

        return review;
    }

    /// <summary>
    /// Take somebody out of the cycle.
    /// </summary>
    /// <remarks>
    /// For the person who left in the first week of it, where a review is a form nobody is going
    /// to fill in and it sits on the chase list for ever. Refused once anything has been written,
    /// because at that point it is somebody's account of their own year.
    /// </remarks>
    public void Exclude(Guid employeeId)
    {
        Refuse();

        if (For(employeeId) is not { } review)
        {
            return;
        }

        if (review.HasSelfNote || review.HasManagerNote)
        {
            throw new InvalidOperationException(
                "Something has already been written in this review. It stays in the cycle — "
                + "removing it would delete somebody's account of their own year.");
        }

        _reviews.Remove(review);
    }

    public void WriteSelf(Guid employeeId, string note, DateTimeOffset at)
    {
        Refuse();

        Required(employeeId).WriteSelf(employeeId, note, at);
    }

    public void WriteManager(
        Guid employeeId, Guid byEmployeeId, string note, ReviewRating? rating, DateTimeOffset at)
    {
        Refuse();

        Required(employeeId).WriteManager(byEmployeeId, note, rating, at);
    }

    public void Share(Guid employeeId, DateTimeOffset at)
    {
        Refuse();

        Required(employeeId).Share(at);

        Raise(new ReviewShared(Id, Name, employeeId, at));
    }

    public void Rename(string name) =>
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("A cycle needs a name.", nameof(name))
            : name.Trim();

    /// <summary>
    /// Close the cycle.
    /// </summary>
    /// <remarks>
    /// Refused while anybody's review is written and unshared. That is the fault worth refusing:
    /// a closed cycle containing a review the subject was never shown is a manager's private
    /// verdict on somebody, kept in a system that person can log into, about a conversation that
    /// never happened.
    /// </remarks>
    public void Close(DateTimeOffset at)
    {
        if (IsClosed)
        {
            return;
        }

        if (NotShared.Count is var unshared and > 0)
        {
            /*
             * Written out rather than assembled from a count and a plural suffix. The first
             * version produced "1 review has been written and not shared … Share them", which
             * gets both halves wrong in one sentence — and this is a refusal somebody reads while
             * already annoyed that the button did not work.
             */
            throw new InvalidOperationException(
                unshared == 1
                    ? "One review has been written and not shared. Closing the cycle would leave "
                      + "a verdict about somebody that they were never shown. Share it, or clear "
                      + "what was written."
                    : $"{unshared} reviews have been written and not shared. Closing the cycle "
                      + "would leave verdicts about people that they were never shown. Share "
                      + "them, or clear what was written.");
        }

        IsClosed = true;
        ClosedAt = at;
    }

    public void Reopen()
    {
        IsClosed = false;
        ClosedAt = null;
    }

    /// <summary>
    /// The cycle's own facts are not secret; what is in a review is guarded by the accessor.
    /// </summary>
    /// <remarks>
    /// The notes are not excluded here because they are not properties of this entity — a review
    /// is a child row, and what the trail records about one is that it changed. Which is the right
    /// amount: that somebody wrote a review is a fact worth keeping, and the words are behind an
    /// accessor that knows who is asking.
    /// </remarks>
    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private Review Required(Guid employeeId) =>
        For(employeeId)
        ?? throw new InvalidOperationException("That person is not in this cycle.");

    private void Refuse()
    {
        if (IsClosed)
        {
            throw new InvalidOperationException(
                $"{Name} is closed. Reopen it, or use the current cycle.");
        }
    }
}

/// <summary>
/// Somebody's review was shared with them.
/// </summary>
/// <remarks>
/// Worth an event because it is the moment the person is owed a conversation, and because it is
/// the one thing in this feature they should hear about without having to look.
/// </remarks>
public sealed record ReviewShared(
    Guid CycleId, string Cycle, Guid EmployeeId, DateTimeOffset At) : DomainEvent;
