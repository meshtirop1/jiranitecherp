using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.Automation;
using JiranisokoTech.Domain.Money;
using JiranisokoTech.Domain.Renewals;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Infrastructure.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Automation;

/// <summary>
/// Put the delayed runs whose time has come on the outbox.
/// </summary>
/// <remarks>
/// The "delays" of section 31. A rule can wait up to thirty days after its event — "a week
/// after somebody starts, ask how it is going" — and the outbox has no way to hold a message
/// back until a date, so a run waits in its own table and this moves it along. It goes through
/// the outbox from here like any other, so a delayed run is retried and set aside exactly as an
/// immediate one is.
///
/// Every five minutes, which is what "delayed by an hour" can honestly promise: somewhere
/// between the hour and five minutes after it.
/// </remarks>
public sealed class ReleaseDelayedAutomation(AppDbContext database, IClock clock) : IRecurringJob
{
    private const int Batch = 200;

    public string Name => "automation.delayed";

    public string Description => "Starts the automation runs that were waiting out a delay.";

    public TimeSpan Every => TimeSpan.FromMinutes(5);

    public async Task<string> RunAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.Now;

        var due = await database.AutomationRuns
            .Where(one => one.Status == AutomationRunStatus.Waiting && one.DueAt <= now)
            .OrderBy(one => one.DueAt)
            .Take(Batch)
            .ToListAsync(cancellationToken);

        if (due.Count == 0)
        {
            return "Nothing was waiting.";
        }

        var ruleIds = due.Select(one => one.RuleId).Distinct().ToList();
        var on = await database.AutomationRules
            .AsNoTracking()
            .Where(one => ruleIds.Contains(one.Id) && one.IsOn)
            .Select(one => one.Id)
            .ToListAsync(cancellationToken);

        var started = 0;

        foreach (var run in due)
        {
            if (on.Contains(run.RuleId))
            {
                run.Queue(now);
                started++;
            }
            else
            {
                // Said on the run, so its history does not simply stop.
                run.Cancel("The rule was switched off while this run was waiting.", now);
            }
        }

        await database.SaveChangesAsync(cancellationToken);

        return started == due.Count
            ? $"Started {started} waiting run(s)."
            : $"Started {started}; cancelled {due.Count - started} whose rule was switched off.";
    }
}

/// <summary>
/// Say which invoices have reached a rung of the overdue ladder today.
/// </summary>
/// <remarks>
/// The scheduled half of section 31. "WHEN invoice overdue" has nothing to hang on otherwise:
/// being overdue is the absence of a payment, and an absence raises no event. This raises
/// <see cref="InvoiceOverdue"/> at seven, twenty-one and forty-five days, once per rung per
/// invoice, recorded in the same ledger of reminders the expiry jobs use so a restart does not
/// announce an invoice twice.
///
/// <b>Only when a rule is listening.</b> With no rule switched on for overdue invoices this
/// does nothing and records nothing, so switching one on later picks up every invoice at the
/// rung it has reached rather than finding them all marked as already told. The number of
/// rules listening is what the ledger records as "told", which is what that column means.
/// </remarks>
public sealed class AnnounceOverdueInvoices(AppDbContext database, IClock clock) : IRecurringJob
{
    public string Name => "automation.invoices_overdue";

    public string Description =>
        "Tells the automation rules which invoices are 7, 21 or 45 days overdue.";

    public TimeSpan Every => TimeSpan.FromDays(1);

    public async Task<string> RunAsync(CancellationToken cancellationToken = default)
    {
        var listening = await database.AutomationRules.CountAsync(
            one => one.IsOn && one.Trigger == nameof(InvoiceOverdue), cancellationToken);

        if (listening == 0)
        {
            return "No rule is switched on for overdue invoices, so none were looked for.";
        }

        var today = clock.Today;
        var ladder = ReminderLadder.For(ReminderKind.InvoiceOverdue);

        var late = await database.Invoices
            .AsNoTracking()
            .Where(one => (one.Status == InvoiceStatus.Sent || one.Status == InvoiceStatus.PartlyPaid)
                && one.DueOn < today)
            .OrderBy(one => one.DueOn)
            .ToListAsync(cancellationToken);

        var announced = 0;

        foreach (var invoice in late)
        {
            if (ladder.StageDueOn(today, invoice.DueOn) is not { } stage)
            {
                continue;
            }

            if (await Reminders.AlreadyToldAsync(
                database, ReminderKind.InvoiceOverdue, invoice.Id, invoice.DueOn, stage,
                cancellationToken))
            {
                continue;
            }

            database.Announce(new InvoiceOverdue(
                invoice.Id,
                invoice.ClientId,
                invoice.Number,
                invoice.DueOn,
                today.DayNumber - invoice.DueOn.DayNumber,
                stage,
                invoice.Outstanding.MinorUnits,
                invoice.Currency));

            database.Reminders.Add(Reminder.Issued(
                ReminderKind.InvoiceOverdue,
                invoice.Id,
                invoice.Number,
                stage,
                invoice.DueOn,
                listening,
                clock.Now));

            announced++;
        }

        if (announced > 0)
        {
            await database.SaveChangesAsync(cancellationToken);
        }

        return late.Count == 0
            ? "No invoice is overdue."
            : $"{late.Count} overdue; {announced} reached a new rung today.";
    }
}
