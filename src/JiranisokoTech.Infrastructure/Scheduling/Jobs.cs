using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Mail;
using JiranisokoTech.Domain.Contracts;
using JiranisokoTech.Application.Accounting;
using JiranisokoTech.Domain.Renewals;
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
        var ladder = ReminderLadder.For(ReminderKind.QualificationLapsing);
        var furthest = today.AddDays(ladder.FirstDaysBefore);

        var lapsing = await database.Employees
            .AsNoTracking()
            .SelectMany(
                employee => employee.Certifications,
                (employee, held) => new
                {
                    employee.Id,
                    employee.FullName,
                    held.Name,
                    held.Issuer,
                    held.ExpiresOn,
                })
            .Where(held => held.ExpiresOn != null
                && held.ExpiresOn >= today
                && held.ExpiresOn <= furthest)
            .OrderBy(held => held.ExpiresOn)
            .ToListAsync(cancellationToken);

        /*
         * Narrowed to the ones at a rung of the ladder that has not been sent yet.
         *
         * This job used to mail every department head about everything lapsing within two
         * months, every single morning — about sixty identical emails per qualification. Its
         * own interface says a job must be safe to run twice and that running twice finds
         * nothing the second time; this is what makes that true.
         */
        var due = new List<(Guid Holder, string Subject, DateOnly On, ReminderStage Stage)>();

        foreach (var held in lapsing)
        {
            if (ladder.StageDueOn(today, held.ExpiresOn!.Value) is not { } stage)
            {
                continue;
            }

            var subject = $"{held.Name} ({held.Issuer}) — {held.FullName}";

            if (await Reminders.AlreadyToldAsync(
                database,
                ReminderKind.QualificationLapsing,
                held.Id,
                held.ExpiresOn.Value,
                stage,
                cancellationToken))
            {
                continue;
            }

            due.Add((held.Id, subject, held.ExpiresOn.Value, stage));
        }

        if (due.Count == 0)
        {
            return lapsing.Count == 0
                ? "Nothing lapses in the next two months."
                : $"{lapsing.Count} lapsing; everybody who needs telling has been told.";
        }

        /*
         * Sent to whoever keeps the staff records rather than to each person. A
         * certification is the firm's evidence as much as the individual's, and the
         * person who has to chase a renewal is in HR — telling only the holder means the
         * firm finds out it has lapsed at the same moment a client does.
         */
        var heads = await Reminders.HeadsOfDepartmentsAsync(database, cancellationToken);

        var lines = due.Select(one => $"  {one.Subject}, lapses {one.On:d MMMM yyyy}");

        var told = 0;

        foreach (var (address, name) in heads.Reachable)
        {
            await mailer.SendAsync(
                Letters.QualificationsLapsing(address, name, [.. lines]),
                cancellationToken);

            told++;
        }

        if (told == 0)
        {
            /*
             * Nothing is written when nobody was told, and that is the important half. A
             * reminder row recorded against an email that never went out would silence the
             * notice for good — so a firm with no department head configured would be warned
             * about nothing, for ever, with a job reporting success every morning.
             */
            return $"{due.Count} at a reminder stage and no department head with a mailbox "
                + "here to tell.";
        }

        foreach (var one in due)
        {
            database.Reminders.Add(Reminder.Issued(
                ReminderKind.QualificationLapsing,
                one.Holder,
                one.Subject,
                one.Stage,
                one.On,
                told,
                clock.Now));
        }

        await database.SaveChangesAsync(cancellationToken);

        return heads.Said(due.Count, told);
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
        var ladder = ReminderLadder.For(ReminderKind.ContractRenewal);
        var furthest = today.AddDays(ladder.FirstDaysBefore);

        var expiring = await database.Contracts
            .AsNoTracking()
            .Where(contract => contract.State == ContractState.Active
                && contract.EndsOn != null
                && contract.EndsOn >= today
                && contract.EndsOn <= furthest)
            .OrderBy(contract => contract.EndsOn)
            .Select(contract => new
            {
                contract.Id,
                contract.Reference,
                contract.Title,
                contract.EndsOn,
            })
            .ToListAsync(cancellationToken);

        /*
         * Only the ones standing at a rung nobody has been told about.
         *
         * This job mailed every department head about every contract ending within
         * forty-five days, every morning — forty-five identical emails about one contract,
         * which is how a reminder becomes a filter rule. Ninety, forty-five and fourteen days
         * out is three, and the ledger is what keeps it at three.
         */
        var due = new List<(Guid Id, string Subject, DateOnly On, ReminderStage Stage)>();

        foreach (var contract in expiring)
        {
            if (ladder.StageDueOn(today, contract.EndsOn!.Value) is not { } stage)
            {
                continue;
            }

            if (await Reminders.AlreadyToldAsync(
                database,
                ReminderKind.ContractRenewal,
                contract.Id,
                contract.EndsOn.Value,
                stage,
                cancellationToken))
            {
                continue;
            }

            due.Add((contract.Id, $"{contract.Reference} — {contract.Title}",
                contract.EndsOn.Value, stage));
        }

        if (due.Count == 0)
        {
            return expiring.Count == 0
                ? "No contract runs out in the next three months."
                : $"{expiring.Count} running out; everybody who needs telling has been told.";
        }

        var heads = await Reminders.HeadsOfDepartmentsAsync(database, cancellationToken);

        var lines = due.Select(one => $"  {one.Subject}, ends {one.On:d MMMM yyyy}");

        var told = 0;

        foreach (var (address, name) in heads.Reachable)
        {
            await mailer.SendAsync(
                Letters.ContractsExpiring(address, name, [.. lines]),
                cancellationToken);

            told++;
        }

        if (told == 0)
        {
            // Nothing recorded when nothing was sent — see the note in the job above.
            return $"{due.Count} at a reminder stage and no department head with a mailbox "
                + "here to tell.";
        }

        foreach (var one in due)
        {
            database.Reminders.Add(Reminder.Issued(
                ReminderKind.ContractRenewal,
                one.Id,
                one.Subject,
                one.Stage,
                one.On,
                told,
                clock.Now));
        }

        await database.SaveChangesAsync(cancellationToken);

        return heads.Said(due.Count, told);
    }
}

/// <summary>
/// Reading the ledger of notices already given.
/// </summary>
/// <remarks>
/// One helper rather than a method on each job, because three jobs ask the same question and
/// the answer has to be the same in all three — a job that phrased the check differently
/// would be the one that sent a second notice, and the symptom would be indistinguishable
/// from the fault this whole ledger was added to fix.
/// </remarks>
internal static class Reminders
{
    public static Task<bool> AlreadyToldAsync(
        AppDbContext database,
        ReminderKind kind,
        Guid subjectId,
        DateOnly deadlineOn,
        ReminderStage stage,
        CancellationToken cancellationToken) =>
        database.Reminders
            .AsNoTracking()
            .AnyAsync(
                one => one.Kind == kind
                    && one.SubjectId == subjectId
                    && one.DeadlineOn == deadlineOn
                    && one.Stage == stage,
                cancellationToken);

    /// <summary>
    /// The department heads, at the addresses they sign in with.
    /// </summary>
    /// <remarks>
    /// <b>This corrects a fault in the two reminder jobs as first written.</b> Both of them
    /// read <c>Employee.Details.PersonalEmail</c> — the private address somebody types into
    /// their own profile beside their date of birth and their next of kin — and mailed
    /// internal notices about their colleagues to it. A list of who holds which
    /// certification and when it lapses is the firm's business and belongs in the firm's
    /// mail; sending it to a personal inbox puts staff records outside anything this firm
    /// controls, and does it on a schedule, every morning, to an address the recipient gave
    /// for an entirely different purpose.
    ///
    /// The address to use is the one on the account, which is where every other letter in
    /// this system already goes — see <c>MailRecipients</c>, which had the right answer the
    /// whole time and was not asked. Not asking it also inherited the wrong failure mode:
    /// the old filter silently dropped any head without a personal address, so the notice
    /// went to whoever happened to have filled that field in.
    ///
    /// Heads without a mailbox are counted rather than dropped quietly, and the count goes
    /// into the job's own sentence on the machinery screen. An employee and an account are
    /// separate things here on purpose, so a head with no login is an ordinary state — but a
    /// firm where three of four heads have no mailbox is a firm whose reminders reach one
    /// person, and nothing said so.
    /// </remarks>
    public static async Task<HeadsToTell> HeadsOfDepartmentsAsync(
        AppDbContext database, CancellationToken cancellationToken)
    {
        var heads = await database.Employees
            .AsNoTracking()
            .Where(employee => database.Departments.Any(department =>
                department.HeadEmployeeId == employee.Id))
            .Select(employee => new
            {
                employee.FullName,

                /*
                 * A correlated subquery rather than a join, so that a head with no account
                 * still comes back — as a row with no address. A join would drop them, and
                 * dropping them is the half of the original fault that nothing could see.
                 *
                 * Filtered on IsActive for the reason MailRecipients gives: somebody whose
                 * access has been withdrawn cannot act on the notice, and a system that
                 * goes on mailing a leaver is one somebody has to explain.
                 */
                Address = database.Users
                    .Where(account => account.Id == employee.AccountId && account.IsActive)
                    .Select(account => account.Email)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        return new HeadsToTell(
            [.. heads
                .Where(head => head.Address is { Length: > 0 })
                .Select(head => (head.Address!, head.FullName))],
            heads.Count(head => head.Address is null or { Length: 0 }));
    }
}

/// <summary>
/// Who a reminder can reach, and how many it cannot.
/// </summary>
/// <param name="WithoutAMailbox">
/// Department heads with no active account, so no address this system knows. Carried rather
/// than discarded because the job's own sentence has to be able to say it: a reminder that
/// reached one person out of four is not the same event as one that reached everybody, and
/// the screen showing "told 1" every morning cannot tell them apart on its own.
/// </param>
public sealed record HeadsToTell(
    IReadOnlyList<(string Address, string Name)> Reachable,
    int WithoutAMailbox)
{
    /// <summary>What the job says it did, including who it could not reach.</summary>
    public string Said(int due, int told)
    {
        var sentence = $"{due} at a reminder stage; told {told}.";

        return WithoutAMailbox == 0
            ? sentence
            : sentence
              + $" {WithoutAMailbox} department "
              + (WithoutAMailbox == 1 ? "head has" : "heads have")
              + " no mailbox here and heard nothing.";
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

/// <summary>
/// Raise the standing costs that have fallen due.
/// </summary>
/// <remarks>
/// The job that makes section 18's recurring expenses more than a list somebody reads. Rent,
/// a domain renewal, a subscription: money that leaves every month whether or not anybody
/// remembers it, and which was previously an expense claim typed from memory — so the month
/// somebody was on leave is a month the cost report is wrong, and a cost missing from a report
/// looks exactly like a cost that was not incurred.
///
/// Safe to run twice, which its interface demands and which the aggregate provides: a charge
/// already raised for a date is refused, so a scheduler restarted twice in a morning does not
/// post three months' rent. The catch-up is the other half — a job that had been off for two
/// months raises the two charges it missed, each carrying the date it was actually due.
/// </remarks>
public sealed class RaiseRecurringExpenses(AccountingService accounting) : IRecurringJob
{
    public string Name => "expenses.recurring";

    public string Description => "Raises the standing costs that have fallen due.";

    /// <remarks>
    /// Daily, not monthly. A monthly timer would have to fire on a particular day and would
    /// miss the month it was restarted in; a daily one asks a question whose answer is usually
    /// "nothing" and is right on the first morning after any outage.
    /// </remarks>
    public TimeSpan Every => TimeSpan.FromDays(1);

    public async Task<string> RunAsync(CancellationToken cancellationToken = default)
    {
        var raised = await accounting.RaiseWhatIsDueAsync(cancellationToken);

        return raised == 0
            ? "Nothing has fallen due."
            : $"Raised {raised} charge(s).";
    }
}
