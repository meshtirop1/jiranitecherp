using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Mail;
using JiranisokoTech.Domain.Contracts;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Scheduling;

/// <summary>
/// Tell somebody when a qualification is about to lapse.
/// </summary>
/// <remarks>
/// The job that justifies the scheduler existing. A certification's expiry was already
/// recorded and already shown on the person's record, and neither of those helps: nobody
/// opens a colleague's record to check a date they have no reason to suspect. The moment
/// it matters is the moment a client asks for evidence during a tender, and by then it
/// has lapsed.
///
/// Two months' notice, which is what it takes to book and sit most examinations.
/// </remarks>
public sealed class WarnAboutLapsingQualifications(
    AppDbContext database,
    IMailer mailer,
    IClock clock)
    : IRecurringJob
{
    public string Name => "qualifications.lapsing";

    public string Description =>
        "Tells the people who keep the staff records which qualifications lapse soon.";

    /// <summary>
    /// Daily.
    /// </summary>
    /// <remarks>
    /// Not hourly: nothing about a date two months away changes within a day, and a
    /// reminder that arrived twenty-four times would be read none of them.
    /// </remarks>
    public TimeSpan Every => TimeSpan.FromDays(1);

    public async Task<string> RunAsync(CancellationToken cancellationToken = default)
    {
        var today = clock.Today;
        var soon = today.AddMonths(2);

        var lapsing = await database.Employees
            .AsNoTracking()
            .SelectMany(
                employee => employee.Certifications,
                (employee, held) => new
                {
                    employee.FullName,
                    held.Name,
                    held.Issuer,
                    held.ExpiresOn,
                })
            .Where(held => held.ExpiresOn != null
                && held.ExpiresOn >= today
                && held.ExpiresOn <= soon)
            .OrderBy(held => held.ExpiresOn)
            .ToListAsync(cancellationToken);

        if (lapsing.Count == 0)
        {
            return "Nothing lapses in the next two months.";
        }

        /*
         * Sent to whoever keeps the staff records rather than to each person. A
         * certification is the firm's evidence as much as the individual's, and the
         * person who has to chase a renewal is in HR — telling only the holder means the
         * firm finds out it has lapsed at the same moment a client does.
         */
        var recipients = await database.Employees
            .AsNoTracking()
            .Where(employee => database.Departments.Any(department =>
                department.HeadEmployeeId == employee.Id))
            .Select(employee => new { employee.FullName, employee.Details.PersonalEmail })
            .ToListAsync(cancellationToken);

        var lines = lapsing.Select(held =>
            $"  {held.Name} ({held.Issuer}) — {held.FullName}, "
            + $"lapses {held.ExpiresOn:d MMMM yyyy}");

        var told = 0;

        foreach (var recipient in recipients.Where(one => one.PersonalEmail is { Length: > 0 }))
        {
            await mailer.SendAsync(
                Letters.QualificationsLapsing(
                    recipient.PersonalEmail!, recipient.FullName, [.. lines]),
                cancellationToken);

            told++;
        }

        return told == 0
            ? $"{lapsing.Count} lapsing and nobody with an address to tell."
            : $"{lapsing.Count} lapsing; told {told}.";
    }
}

/// <summary>
/// Tell somebody when a client contract is about to run out.
/// </summary>
/// <remarks>
/// The commercial twin of the one above, and the more expensive absence of the two. Work
/// continuing past the end of the contract that authorises it is work the client can
/// decline to pay for, and it is discovered at the invoice rather than at the date.
/// </remarks>
public sealed class WarnAboutExpiringContracts(
    AppDbContext database,
    IMailer mailer,
    IClock clock)
    : IRecurringJob
{
    public string Name => "contracts.expiring";

    public string Description =>
        "Tells the people who keep the staff records which client contracts run out soon.";

    public TimeSpan Every => TimeSpan.FromDays(1);

    public async Task<string> RunAsync(CancellationToken cancellationToken = default)
    {
        var today = clock.Today;
        var soon = today.AddDays(45);

        var expiring = await database.Contracts
            .AsNoTracking()
            .Where(contract => contract.State == ContractState.Active
                && contract.EndsOn != null
                && contract.EndsOn >= today
                && contract.EndsOn <= soon)
            .OrderBy(contract => contract.EndsOn)
            .Select(contract => new { contract.Reference, contract.Title, contract.EndsOn })
            .ToListAsync(cancellationToken);

        if (expiring.Count == 0)
        {
            return "No contract runs out in the next six weeks.";
        }

        var recipients = await database.Employees
            .AsNoTracking()
            .Where(employee => database.Departments.Any(department =>
                department.HeadEmployeeId == employee.Id))
            .Select(employee => new { employee.FullName, employee.Details.PersonalEmail })
            .ToListAsync(cancellationToken);

        var lines = expiring.Select(contract =>
            $"  {contract.Reference} — {contract.Title}, ends {contract.EndsOn:d MMMM yyyy}");

        var told = 0;

        foreach (var recipient in recipients.Where(one => one.PersonalEmail is { Length: > 0 }))
        {
            await mailer.SendAsync(
                Letters.ContractsExpiring(
                    recipient.PersonalEmail!, recipient.FullName, [.. lines]),
                cancellationToken);

            told++;
        }

        return told == 0
            ? $"{expiring.Count} running out and nobody with an address to tell."
            : $"{expiring.Count} running out; told {told}.";
    }
}

/// <summary>
/// Delete run history that is no longer of use.
/// </summary>
/// <remarks>
/// A scheduler that recorded every run forever would fill the largest table in the
/// database with the fact that nothing happened. Ninety days is long enough to answer
/// "when did this stop working", which is the only question the history is asked.
///
/// Failures are kept regardless of age, for the same reason the outbox keeps abandoned
/// messages: they are the ones somebody has to look at, they are rare, and deleting the
/// evidence of a fault on a timer is how a system loses the only record that it went
/// wrong.
/// </remarks>
public sealed class PruneJobHistory(AppDbContext database, IClock clock) : IRecurringJob
{
    public string Name => "jobs.prune";

    public string Description => "Deletes successful run history older than ninety days.";

    public TimeSpan Every => TimeSpan.FromDays(1);

    public async Task<string> RunAsync(CancellationToken cancellationToken = default)
    {
        var before = clock.Now.AddDays(-90);

        var removed = await database.Set<JobRun>()
            .Where(run => run.Outcome == JobOutcome.Ran && run.At < before)
            .ExecuteDeleteAsync(cancellationToken);

        return removed == 0
            ? "Nothing old enough to delete."
            : $"Deleted {removed} run(s) older than ninety days.";
    }
}
