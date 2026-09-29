using System.Text.Json;
using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Automation;
using JiranisokoTech.Domain.Automation;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Infrastructure.Messaging;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JiranisokoTech.Infrastructure.Automation;

/// <summary>
/// Carry out one run of one rule, action by action, recording each.
/// </summary>
/// <remarks>
/// A handler of <see cref="AutomationRunDue"/>, so it is the outbox that retries a failed run,
/// with its backoff, and sets it aside after its number of attempts — the same semantics as
/// every other reaction here rather than a second retry policy with opinions of its own. The
/// run mirrors the outbox's count, so its history says "gave up" at the moment the machinery
/// screen starts counting the message as abandoned, and putting the message back from that
/// screen runs what is left of it.
///
/// <b>What is safe to retry.</b> Each action's step is saved as it finishes, and a retry skips
/// the finished ones. A work item's identifier is saved the moment it exists, so a retry after
/// a later failure finds the item and does not raise a second. Notices and emails are sent
/// again if the failure fell in the middle of their own list, which is the duplicate the rest
/// of this application's mail already accepts over the alternative of a letter never sent.
///
/// <b>Who it acts as.</b> Nobody is signed in on the outbox's thread. The rule's name is put
/// in <see cref="Causation"/> as the actor, which the audit trail records on every row an
/// action writes, and the rule's own id is added to the chain stamped on every event an action
/// raises — which is how the rule recognises its own work if it comes back.
///
/// <b>Isolation.</b> The run is read and written in a scope of its own, and each action gets a
/// fresh one. The outbox dispatcher's scope is holding the message it will mark afterwards; an
/// action that failed halfway through would otherwise leave half-built entities tracked there,
/// and the dispatcher's next save would try to write them.
/// </remarks>
public sealed class CarryOutAutomation(
    IServiceScopeFactory scopes,
    DomainEventRegistry registry,
    IClock clock,
    IOptions<OutboxOptions> outbox,
    ILogger<CarryOutAutomation> logger)
    : IDomainEventHandler<AutomationRunDue>
{
    public async Task HandleAsync(
        AutomationRunDue domainEvent, CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var run = await database.AutomationRuns
            .FirstOrDefaultAsync(one => one.Id == domainEvent.RunId, cancellationToken);

        if (run is null || run.IsSettled)
        {
            return;
        }

        var rule = await database.AutomationRules
            .AsNoTracking()
            .FirstOrDefaultAsync(one => one.Id == run.RuleId, cancellationToken);

        if (rule is not { IsOn: true })
        {
            run.Cancel("The rule was switched off before this run came due.", clock.Now);
            await database.SaveChangesAsync(cancellationToken);
            return;
        }

        if (Rebuild(run) is not { } facts)
        {
            run.Cancel(
                $"\"{run.Trigger}\" is no longer something a rule can start from, so the event "
                + "this run was kept for cannot be read.",
                clock.Now);
            await database.SaveChangesAsync(cancellationToken);
            return;
        }

        using (Causation.Enter(new Cause(
            Causation.Current?.MessageId,
            [.. run.ChainIds, rule.Id],
            $"Automation rule: {rule.Name}")))
        {
            foreach (var action in rule.Actions)
            {
                var step = run.Begin(action, Wording.Describe(facts.Trigger, action));

                if (step.IsDone)
                {
                    continue;
                }

                try
                {
                    using var own = scopes.CreateScope();

                    var outcome = await own.ServiceProvider
                        .GetRequiredService<ActionPerformer>()
                        .PerformAsync(
                            rule,
                            action,
                            facts,
                            step.MadeId,
                            async id =>
                            {
                                step.Made(id);
                                await database.SaveChangesAsync(cancellationToken);
                            },
                            cancellationToken);

                    step.Did(outcome, clock.Now);
                }
                catch (Exception refused) when (refused is InvalidOperationException or ArgumentException)
                {
                    step.WasRefused(refused.Message, clock.Now);

                    logger.LogInformation(
                        "Automation rule {Rule} was refused \"{Action}\": {Why}",
                        rule.Name,
                        step.Describing,
                        refused.Message);
                }
                catch (Exception failed) when (failed is not OperationCanceledException)
                {
                    run.Failed($"{step.Describing}: {failed.Message}", outbox.Value.MaxAttempts);
                    await database.SaveChangesAsync(cancellationToken);

                    // Thrown on, so the outbox retries this run with its backoff, and gives up
                    // on it after as many attempts as the run has just counted.
                    throw;
                }

                await database.SaveChangesAsync(cancellationToken);
            }

            run.Finish(clock.Now);
            await database.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>The event the run was kept for, rebuilt, or null when it can no longer be.</summary>
    private Facts? Rebuild(AutomationRun run)
    {
        if (Triggers.Find(run.Trigger) is not { } trigger || registry.Find(run.Trigger) is not { } type)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(run.Payload, type, AppDbContext.JsonOptions)
                is IDomainEvent rebuilt
                ? Application.Automation.Facts.Of(trigger, rebuilt)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
