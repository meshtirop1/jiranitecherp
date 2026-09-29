using System.Text.Json;
using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Automation;
using JiranisokoTech.Domain.Automation;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JiranisokoTech.Infrastructure.Automation;

/// <summary>
/// WHEN and IF: find the rules an event fires, and write down a run for each.
/// </summary>
/// <remarks>
/// Generic, and registered once for every event in <see cref="Triggers.All"/>, so the list
/// of what a rule may start from is that file and nothing else. It only decides; nothing is
/// done here. Each match becomes a run whose own event goes back through the outbox, and the
/// actions are carried out by <see cref="CarryOutAutomation"/> from there — so one rule's
/// failing email is retried on its own, rather than making the outbox deliver this event again
/// to every other handler that already succeeded.
///
/// <b>Idempotent, as every handler must be.</b> The outbox runs this again whenever any other
/// handler of the same event throws. A run is keyed on the rule and the outbox message, checked
/// here and enforced by a unique index, so the second sighting does nothing.
///
/// <b>Where the loop protection lives.</b> An event carries the chain of rules whose actions
/// led to it. A rule already in that chain is not run again — its own work has come back to
/// it — and no rule is run once the chain is <see cref="AutomationRun.DeepestChain"/> long.
/// Neither is silent: each is recorded as a suppressed run saying why, which is what somebody
/// debugging "why did my rule not fire" needs to find.
/// </remarks>
public sealed class MatchRules<TEvent>(
    AppDbContext database,
    IClock clock,
    ILogger<MatchRules<TEvent>> logger)
    : IDomainEventHandler<TEvent>
    where TEvent : IDomainEvent
{
    public async Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken = default)
    {
        var name = typeof(TEvent).Name;

        if (Triggers.Find(name) is not { } trigger)
        {
            return;
        }

        var rules = await database.AutomationRules
            .AsNoTracking()
            .Where(one => one.IsOn && one.Trigger == name)
            .ToListAsync(cancellationToken);

        if (rules.Count == 0)
        {
            return;
        }

        var cause = Causation.Current;

        /*
         * Outside the dispatcher there is no message, which only a test that calls the handler
         * directly produces. A fresh identifier means such a call is never mistaken for one the
         * rule has already seen; the idempotency the outbox needs does not arise there.
         */
        var messageId = cause?.MessageId ?? Guid.CreateVersion7();
        var chain = cause?.Chain ?? [];
        var facts = Facts.Of(trigger, domainEvent);
        var summary = Wording.Fill(trigger.Summary, facts, string.Empty);
        var payload = JsonSerializer.Serialize(domainEvent, AppDbContext.JsonOptions);
        var now = clock.Now;
        var added = 0;

        foreach (var rule in rules)
        {
            if (await database.AutomationRuns.AnyAsync(
                one => one.RuleId == rule.Id && one.SourceMessageId == messageId,
                cancellationToken))
            {
                continue;
            }

            /*
             * A rule acts on what happens after it is switched on. The outbox can be holding
             * events from before — a backlog after an outage, or the minute between somebody
             * hiring three people and somebody else switching the joiners' rule on — and a rule
             * that reached back for those would raise work for joiners who were set up by hand
             * last week. Found by the end-to-end test, where it did exactly that.
             */
            if (rule.SwitchedOnAt is { } on && domainEvent.OccurredAt < on)
            {
                continue;
            }

            if (!Conditions.AllHold(rule.Conditions, facts))
            {
                continue;
            }

            var held = await WhyHeldAsync(rule, chain, now, cancellationToken);

            database.AutomationRuns.Add(held is null
                ? AutomationRun.Matched(rule, messageId, payload, summary, chain, now)
                : AutomationRun.Held(rule, messageId, summary, chain, held, now));

            if (held is not null)
            {
                logger.LogWarning(
                    "Automation rule {Rule} matched {Event} and was not run: {Why}",
                    rule.Name,
                    name,
                    held);
            }

            added++;
        }

        if (added > 0)
        {
            await database.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<string?> WhyHeldAsync(
        AutomationRule rule,
        IReadOnlyList<Guid> chain,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (chain.Contains(rule.Id))
        {
            return "This event was caused by this rule's own actions, directly or through other "
                + "rules. Running it again would be a loop.";
        }

        if (chain.Count >= AutomationRun.DeepestChain)
        {
            return $"This event came at the end of a chain of {chain.Count} rules, each firing "
                + $"the next. Chains stop at {AutomationRun.DeepestChain}, so that two rules "
                + "feeding each other cannot run for ever.";
        }

        var since = now.AddHours(-1);
        var lastHour = await database.AutomationRuns.CountAsync(
            one => one.RuleId == rule.Id
                && one.MatchedAt >= since
                && one.Status != AutomationRunStatus.Suppressed,
            cancellationToken);

        return lastHour >= AutomationRun.MostPerHour
            ? $"This rule has already fired {lastHour} times in the last hour, which is as many as "
                + "one rule may. Something upstream is producing events in bulk — an import, or "
                + "another rule — and answering each one would bury the people it tells."
            : null;
    }
}
