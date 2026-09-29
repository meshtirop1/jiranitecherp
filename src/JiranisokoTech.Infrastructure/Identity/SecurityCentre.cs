using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.Api;
using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Identity;

/// <summary>
/// What the security centre reads, and the one thing it writes: an access review.
/// </summary>
/// <remarks>
/// Section 28 lists nine things. Most of them existed before this, scattered: each person could
/// see their own sign-ins and nobody could see anybody else's, role grants were on the trail
/// among invoice edits, API keys were under settings, and whether the second factor was on for
/// an account was visible nowhere at all. A firm with one administrator asking "is anything
/// wrong" had nowhere to look. This puts the answers on one page, and adds the parts that did
/// not exist — the firm-wide sign-in history and the access review.
/// </remarks>
public sealed class SecurityCentre(
    AppDbContext database,
    UserDirectory directory,
    UserAdministration administration,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>How long an account can go unused before a review is asked to look at it.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromDays(90);

    /// <summary>
    /// How often a review is due.
    /// </summary>
    /// <remarks>
    /// Quarterly, which is what most audit frameworks ask for and short enough that a role
    /// granted for one month-end is noticed before the next quarter's.
    /// </remarks>
    public static readonly TimeSpan ReviewEvery = TimeSpan.FromDays(90);

    private const int Shown = 50;

    public async Task<SecurityOverview> OverviewAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.Now;
        var accounts = await directory.AllAsync(cancellationToken);
        var names = accounts.ToDictionary(account => account.Id, account => account.DisplayName);

        var signIns = await database.Set<SignInRecord>().AsNoTracking()
            .OrderByDescending(record => record.At)
            .Take(Shown)
            .ToListAsync(cancellationToken);

        var dayAgo = now.AddDays(-1);

        var refusedToday = await database.Set<SignInRecord>().AsNoTracking()
            .CountAsync(record => record.At >= dayAgo
                && record.Outcome != SignInOutcome.Succeeded
                && record.Outcome != SignInOutcome.SecondFactorRequired
                && record.Outcome != SignInOutcome.RecoveryCodeUsed,
                cancellationToken);

        var permissionChanges = await database.AuditEntries.AsNoTracking()
            .Where(entry => entry.Action == "role.granted" || entry.Action == "role.revoked")
            .OrderByDescending(entry => entry.OccurredAt)
            .Take(Shown)
            .ToListAsync(cancellationToken);

        /*
         * Everything else that happened to an account or an API key: withdrawn, restored,
         * locked, unlocked, second factor switched on or off, a key issued or revoked. The
         * account's own columns that move on every sign-in are excluded from the trail, so
         * what arrives here is only the changes somebody would want to know about.
         */
        var events = await database.AuditEntries.AsNoTracking()
            .Where(entry => (entry.SubjectType == nameof(ApplicationUser)
                    && entry.Action != "role.granted" && entry.Action != "role.revoked")
                || entry.SubjectType == nameof(ApiKey))
            .OrderByDescending(entry => entry.OccurredAt)
            .Take(Shown)
            .ToListAsync(cancellationToken);

        var keys = await database.ApiKeys.AsNoTracking()
            .Where(key => key.RevokedAt == null)
            .Select(key => new { key.LastUsedOn })
            .ToListAsync(cancellationToken);

        var last = await database.Set<AccessReview>().AsNoTracking()
            .OrderByDescending(review => review.ReviewedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var active = accounts.Where(account => account.IsActive).ToList();

        return new SecurityOverview(
            signIns,
            refusedToday,
            active.Count(account => account.IsLockedOut),
            active.Where(account => !account.UsesSecondFactor).ToList(),
            active.Where(account => IsQuiet(account, now)).ToList(),
            permissionChanges,
            events,
            names,
            keys.Count,
            keys.Count(key => key.LastUsedOn is null),
            last,
            last is null || now - last.ReviewedAt > ReviewEvery);
    }

    /// <summary>The accounts a review is asked about: every one that can still sign in.</summary>
    public async Task<List<AccountRow>> ToReviewAsync(CancellationToken cancellationToken = default) =>
        (await directory.AllAsync(cancellationToken))
            .Where(account => account.IsActive)
            .ToList();

    public Task<List<AccessReview>> ReviewsAsync(CancellationToken cancellationToken = default) =>
        database.Set<AccessReview>().AsNoTracking()
            .OrderByDescending(review => review.ReviewedAt)
            .Take(20)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Record a review of every active account, withdrawing the ones the reviewer chose.
    /// </summary>
    /// <returns>The review, and the withdrawals that were refused and why.</returns>
    /// <remarks>
    /// Withdrawals go through <see cref="UserAdministration.DeactivateAsync"/>, so its rules
    /// hold here as they do on the account page — nobody withdraws their own account, and the
    /// last owner stays. One refused does not stop the rest; the line records the account as
    /// kept, because it was, and the refusal is handed back to be said on the page. A review
    /// that claimed a withdrawal that had not happened would be worse than no review.
    /// </remarks>
    public async Task<(AccessReview Review, IReadOnlyList<string> Refused)> RecordReviewAsync(
        IReadOnlyCollection<Guid> withdraw, string? note, CancellationToken cancellationToken = default)
    {
        var accounts = await ToReviewAsync(cancellationToken);
        var refused = new List<string>();
        var lines = new List<AccessReviewLine>();

        foreach (var account in accounts)
        {
            var kept = true;

            if (withdraw.Contains(account.Id))
            {
                try
                {
                    await administration.DeactivateAsync(account.Id, currentUser.Id ?? Guid.Empty);
                    kept = false;
                }
                catch (InvalidOperationException refusal)
                {
                    refused.Add($"{account.DisplayName}: {refusal.Message}");
                }
            }

            lines.Add(new AccessReviewLine(
                account.Id, account.Email, account.Roles, account.UsesSecondFactor,
                account.LastSignedInAt, kept));
        }

        var review = AccessReview.Record(
            clock.Now, currentUser.Id, currentUser.Name ?? "Unknown", note, lines);

        database.Add(review);
        await database.SaveChangesAsync(cancellationToken);

        return (review, refused);
    }

    /// <summary>
    /// Active, able to sign in, and has not for a while.
    /// </summary>
    /// <remarks>
    /// An invitation never taken up counts once it is older than the same period, because an
    /// open invitation is an account somebody else could claim with the link.
    /// </remarks>
    public static bool IsQuiet(AccountRow account, DateTimeOffset now) =>
        (account.LastSignedInAt ?? account.InvitedAt) is { } since && now - since > Quiet;
}

public sealed record SecurityOverview(
    IReadOnlyList<SignInRecord> SignIns,
    int RefusedToday,
    int LockedOut,
    IReadOnlyList<AccountRow> WithoutSecondFactor,
    IReadOnlyList<AccountRow> Quiet,
    IReadOnlyList<AuditEntry> PermissionChanges,
    IReadOnlyList<AuditEntry> Events,
    IReadOnlyDictionary<Guid, string> Names,
    int LiveKeys,
    int KeysNeverUsed,
    AccessReview? LastReview,
    bool ReviewDue)
{
    public string NameOf(Guid account) => Names.GetValueOrDefault(account) ?? "An account since forgotten";
}
