namespace JiranisokoTech.Infrastructure.Identity;

/// <summary>
/// Somebody looked at every account and what it can do, and said which should stay.
/// </summary>
/// <remarks>
/// Section 28's access reviews. Access accumulates: a role granted for a month-end close is
/// still there in March, a contractor's account outlives the contract, and nothing about any of
/// it is visible until somebody goes looking. A review is that looking, done on purpose and
/// written down.
///
/// <b>It keeps what was looked at, not only that somebody looked.</b> Each line is a copy of an
/// account as it stood — its address, its roles, whether it had a second factor, when it was
/// last used — and whether the reviewer kept it. A review that recorded only a date and a name
/// could not answer the question an auditor asks of it, which is "was this person's
/// administrator role in front of you when you signed this off".
///
/// Written once and never changed. There is no edit, because a review corrected later is a
/// review that can say whatever turns out to be convenient.
/// </remarks>
public sealed class AccessReview
{
    private readonly List<AccessReviewLine> _lines = [];

    private AccessReview()
    {
        ReviewerName = string.Empty;
    }

    private AccessReview(
        DateTimeOffset at, Guid? reviewerId, string reviewerName, string? note,
        IEnumerable<AccessReviewLine> lines)
    {
        Id = Guid.CreateVersion7();
        ReviewedAt = at;
        ReviewerId = reviewerId;
        ReviewerName = reviewerName;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        _lines.AddRange(lines);
    }

    public Guid Id { get; private init; }

    public DateTimeOffset ReviewedAt { get; private init; }

    public Guid? ReviewerId { get; private init; }

    /// <summary>Their name as it was, so the record reads the same after they have gone.</summary>
    public string ReviewerName { get; private init; }

    public string? Note { get; private init; }

    /// <summary>
    /// A copy, not the list EF tracks.
    /// </summary>
    /// <remarks>
    /// Handing EF the backing list makes it treat added rows as updates, so nothing is saved
    /// and nothing complains — see CLAUDE.md. Nothing adds to this after it is created, but the
    /// shape is the one the codebase uses everywhere, so the next person does not have to think.
    /// </remarks>
    public IReadOnlyList<AccessReviewLine> Lines => _lines.ToList();

    public int Withdrawn => _lines.Count(line => !line.Kept);

    public static AccessReview Record(
        DateTimeOffset at, Guid? reviewerId, string reviewerName, string? note,
        IReadOnlyCollection<AccessReviewLine> lines)
    {
        if (lines.Count == 0)
        {
            // Only possible with no active accounts at all, and then the reviewer is not signed
            // in either. Said anyway: a review of nothing would read as a clean bill of health.
            throw new InvalidOperationException("There are no accounts to review.");
        }

        return new AccessReview(at, reviewerId, reviewerName, note, lines);
    }
}

/// <summary>One account as the reviewer saw it, and what they decided.</summary>
public sealed class AccessReviewLine
{
    private AccessReviewLine()
    {
        Email = string.Empty;
        Roles = string.Empty;
    }

    public AccessReviewLine(
        Guid accountId, string email, IEnumerable<string> roles, bool usesSecondFactor,
        DateTimeOffset? lastSignedInAt, bool kept)
    {
        Id = Guid.CreateVersion7();
        AccountId = accountId;
        Email = email;
        Roles = string.Join(", ", roles.Order());
        UsesSecondFactor = usesSecondFactor;
        LastSignedInAt = lastSignedInAt;
        Kept = kept;
    }

    public Guid Id { get; private init; }

    public Guid AccountId { get; private init; }

    /// <summary>The address at the time. An account's address can change; this cannot.</summary>
    public string Email { get; private init; }

    /// <summary>Written out rather than referenced, for the same reason.</summary>
    public string Roles { get; private init; }

    public bool UsesSecondFactor { get; private init; }

    public DateTimeOffset? LastSignedInAt { get; private init; }

    /// <summary>False when the reviewer withdrew the account as part of this review.</summary>
    public bool Kept { get; private init; }
}
