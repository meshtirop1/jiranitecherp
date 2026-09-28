using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Documents;
using JiranisokoTech.Application.Recruitment;
using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Documents;
using JiranisokoTech.Domain.Recruitment;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Scheduling;

/// <summary>
/// Removes what the firm has decided it no longer needs to keep.
/// </summary>
/// <remarks>
/// Section 53: retention the administrators configure, for applicants, documents, deleted
/// accounts and the audit trail. Application logs are not the database's — Docker keeps them,
/// and how much is set in <c>.env</c>; see docs/configuration.md.
///
/// <b>Nothing happens until somebody decides.</b> Every period is null until it is set on the
/// settings page, and null means keep. A retention job that arrived with defaults would delete
/// records on the first night after an upgrade, which nobody asked for and nobody could undo.
///
/// <b>Erasing is recorded without what was erased.</b> Applicants and accounts are changed
/// through ordinary saves so the audit trail says it happened, and <see cref="IForgettable"/>
/// keeps the erased values out of that record — otherwise the trail would hold the very name
/// and address this was meant to remove.
///
/// Each kind is independent: one that fails does not stop the others, and says so in the run's
/// result on the machinery screen.
/// </remarks>
public sealed class ApplyRetention(
    AppDbContext database,
    ICvStore cvs,
    IDocumentStore documents,
    IClock clock)
    : IRecurringJob
{
    public string Name => "retention.sweep";

    public string Description =>
        "Erases applicants, leavers' documents, withdrawn accounts and audit history kept past "
        + "the periods set on the settings page.";

    public TimeSpan Every => TimeSpan.FromDays(1);

    public async Task<string> RunAsync(CancellationToken cancellationToken = default)
    {
        var settings = await database.Settings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);

        if (settings is null
            || (settings.CandidateRetentionMonths is null
                && settings.LeaverDocumentRetentionYears is null
                && settings.WithdrawnAccountRetentionMonths is null
                && settings.AuditRetentionYears is null))
        {
            return "No retention periods are set, so everything is kept.";
        }

        var said = new List<string>();

        await Each(said, "applicants", settings.CandidateRetentionMonths,
            months => ApplicantsAsync(months, cancellationToken));
        await Each(said, "leavers' documents", settings.LeaverDocumentRetentionYears,
            years => LeaverDocumentsAsync(years, cancellationToken));
        await Each(said, "withdrawn accounts", settings.WithdrawnAccountRetentionMonths,
            months => AccountsAsync(months, cancellationToken));
        await Each(said, "audit entries", settings.AuditRetentionYears,
            years => AuditAsync(years, cancellationToken));

        return string.Join("; ", said) + ".";
    }

    private static async Task Each(
        List<string> said, string what, int? period, Func<int, Task<int>> sweep)
    {
        if (period is not { } chosen)
        {
            said.Add($"{what} kept");
            return;
        }

        try
        {
            var count = await sweep(chosen);
            said.Add($"{count} {what} removed");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            said.Add($"{what} failed: {exception.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// Applicants whose last application closed before the period, and who were never hired.
    /// </summary>
    /// <remarks>
    /// Anybody with an application still in progress is left alone however old their first one
    /// is, and so is anybody who was hired: they are staff now, and their staff record is kept
    /// under different rules. Read into memory to decide, because "the latest date across all of
    /// somebody's applications" is simple in C# and a trial in SQL, and applicants number in the
    /// hundreds.
    /// </remarks>
    private async Task<int> ApplicantsAsync(int months, CancellationToken cancellationToken)
    {
        var cutoff = clock.Now.AddMonths(-months);

        var candidates = await database.Candidates
            .Where(candidate => candidate.ForgottenAt == null)
            .ToListAsync(cancellationToken);

        var applications = await database.Applications
            .Where(application => application.ForgottenAt == null)
            .ToListAsync(cancellationToken);

        var byCandidate = applications.ToLookup(application => application.CandidateId);
        var forgotten = 0;

        foreach (var candidate in candidates)
        {
            var theirs = byCandidate[candidate.Id].ToList();

            var closed = theirs.All(application => application.Status
                is ApplicationStatus.Rejected or ApplicationStatus.Withdrawn);

            var last = theirs.Count == 0
                ? candidate.FirstSeenAt
                : theirs.Max(application => application.DecidedAt ?? application.AppliedAt);

            if (!closed || last >= cutoff)
            {
                continue;
            }

            foreach (var application in theirs)
            {
                if (application.ForgetWhatTheySent(clock.Now) is { } file)
                {
                    await cvs.DeleteAsync(file, cancellationToken);
                }
            }

            candidate.Forget(clock.Now);
            forgotten++;
        }

        await database.SaveChangesAsync(cancellationToken);

        return forgotten;
    }

    /// <summary>
    /// Documents and photographs on the staff records of people who left before the period.
    /// </summary>
    /// <remarks>
    /// Only those two kinds. An employment contract is filed as an agreement, and agreements are
    /// the firm's own record of what it signed rather than part of somebody's personnel file.
    /// </remarks>
    private async Task<int> LeaverDocumentsAsync(int years, CancellationToken cancellationToken)
    {
        var cutoff = clock.Today.AddYears(-years);

        var leavers = await database.Employees
            .Where(employee => employee.LeftOn != null && employee.LeftOn < cutoff)
            .Select(employee => employee.Id)
            .ToListAsync(cancellationToken);

        if (leavers.Count == 0)
        {
            return 0;
        }

        var files = await database.Attachments
            .Where(attachment => (attachment.Kind == AttachedTo.Employee
                    || attachment.Kind == AttachedTo.Photo)
                && leavers.Contains(attachment.OwnerId))
            .ToListAsync(cancellationToken);

        database.Attachments.RemoveRange(files);
        await database.SaveChangesAsync(cancellationToken);

        // After the rows are gone, so that a failure part way leaves orphaned files rather than
        // rows pointing at files that no longer exist — the first is untidy, the second is a
        // broken download for whoever finds it.
        foreach (var file in files)
        {
            await documents.DeleteAsync(file.StoredName, cancellationToken);
        }

        return files.Count;
    }

    /// <summary>
    /// Login details of accounts withdrawn before the period.
    /// </summary>
    /// <remarks>
    /// The row stays: audit entries and approvals point at it by identifier, and a trail whose
    /// actors resolve to nothing is harder to read than one that says "former user". What goes is
    /// what identified the person — the address, the name, the phone number — and the address
    /// becomes one that cannot be delivered to and cannot collide, because it is unique.
    /// </remarks>
    private async Task<int> AccountsAsync(int months, CancellationToken cancellationToken)
    {
        var cutoff = clock.Now.AddMonths(-months);

        var accounts = await database.Users
            .Where(user => !user.IsActive
                && user.ForgottenAt == null
                && user.WithdrawnAt != null
                && user.WithdrawnAt < cutoff)
            .ToListAsync(cancellationToken);

        foreach (var account in accounts)
        {
            var address = $"forgotten-{account.Id:N}@invalid";

            account.Email = address;
            account.NormalizedEmail = address.ToUpperInvariant();
            account.UserName = address;
            account.NormalizedUserName = address.ToUpperInvariant();
            account.DisplayName = "Former user";
            account.PhoneNumber = null;
            account.ForgottenAt = clock.Now;
        }

        await database.SaveChangesAsync(cancellationToken);

        return accounts.Count;
    }

    /// <summary>
    /// Audit entries older than the period, after saying so in the trail itself.
    /// </summary>
    /// <remarks>
    /// The one bulk delete this table allows, and it is written down in RetentionTests. The
    /// entry saying what was removed is saved first, in its own transaction, so that if the
    /// delete then fails the trail says a removal was attempted rather than nothing at all; and
    /// it is dated now, so the delete that follows cannot take it with the rest.
    /// </remarks>
    private async Task<int> AuditAsync(int years, CancellationToken cancellationToken)
    {
        var cutoff = clock.Now.AddYears(-years);

        var count = await database.AuditEntries
            .CountAsync(entry => entry.OccurredAt < cutoff, cancellationToken);

        if (count == 0)
        {
            return 0;
        }

        database.AuditEntries.Add(AuditEntry.Record(
            "audit.pruned",
            nameof(AuditEntry),
            Guid.Empty,
            clock.Now,
            reason: $"{count} entries from before {cutoff:d MMMM yyyy} removed under the "
                + $"{years}-year retention period set on the settings page."));

        await database.SaveChangesAsync(cancellationToken);

        return await database.AuditEntries
            .Where(entry => entry.OccurredAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
