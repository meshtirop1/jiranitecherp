using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.Automation;

namespace JiranisokoTech.Application.Automation;

/// <summary>
/// Writing, changing and switching rules. Carrying them out is elsewhere.
/// </summary>
/// <remarks>
/// Section 31. Every method here is something a person does on the rules page, and every one
/// checks what it is given against the rule's trigger before it is stored: the fields a
/// condition names, the comparison, the value, the names in braces in a title, the people and
/// projects an action points at. A rule is written once and fires for months with nobody
/// watching, so the only good moment to say "that cannot work" is while somebody is looking at
/// the screen.
///
/// What a rule is allowed to do is decided by the actions offered, not by who writes it — a
/// rule acts as the firm, whoever wrote it. That is why writing rules is its own narrow
/// permission rather than a side effect of holding the permissions its actions would need.
/// </remarks>
public sealed class AutomationService(IAutomationRepository rules, IClock clock)
{
    public async Task<AutomationRule> WriteAsync(
        string name, string trigger, string? description,
        CancellationToken cancellationToken = default)
    {
        Triggers.Required(trigger);

        var rule = AutomationRule.Write(name, trigger, description, clock.Now);

        rules.Add(rule);
        await rules.SaveAsync(cancellationToken);

        return rule;
    }

    public async Task DescribeAsync(
        Guid ruleId, string name, string? description, int delayMinutes,
        CancellationToken cancellationToken = default)
    {
        var rule = await Required(ruleId, cancellationToken);

        rule.Describe(name, description, delayMinutes);
        await rules.SaveAsync(cancellationToken);
    }

    public async Task WhenAsync(
        Guid ruleId, string field, ConditionOperator comparison, string? value,
        CancellationToken cancellationToken = default)
    {
        var rule = await Required(ruleId, cancellationToken);
        var trigger = Triggers.Required(rule.Trigger);

        Refuse(Conditions.Refusal(trigger, field, comparison, value));

        rule.When(
            trigger.Field(field)!.Name,
            comparison,
            Conditions.NeedsValue(comparison) ? value : null);

        await rules.SaveAsync(cancellationToken);
    }

    public async Task DropConditionAsync(
        Guid ruleId, Guid conditionId, CancellationToken cancellationToken = default)
    {
        var rule = await Required(ruleId, cancellationToken);

        rule.DropCondition(conditionId);
        await rules.SaveAsync(cancellationToken);
    }

    /// <summary>THEN raise a piece of work.</summary>
    /// <param name="where">Empty for no project, <c>field:…</c> for the event's, <c>project:…</c> for a chosen one.</param>
    /// <param name="who">Who the work is for, or empty for nobody yet.</param>
    /// <param name="dueInDays">When it is due, counted from the day the rule acts.</param>
    public async Task RaiseWorkAsync(
        Guid ruleId, string title, string? detail, string? who, string? where, int? dueInDays,
        CancellationToken cancellationToken = default)
    {
        var (rule, trigger) = await RuleAndTrigger(ruleId, cancellationToken);

        Refuse(string.IsNullOrWhiteSpace(title) ? "Give the work a title." : null);
        Refuse(Wording.Refusal(title, trigger));
        Refuse(Wording.Refusal(detail, trigger));
        Refuse(Who.Refusal(trigger, who, roles: false, required: false));
        await CheckPersonAsync(who, cancellationToken);
        await CheckProjectAsync(trigger, where, cancellationToken);

        rule.Then(AutomationActionKind.RaiseWork, title, detail, who, where, dueInDays);
        await rules.SaveAsync(cancellationToken);
    }

    /// <summary>THEN put a notice in front of people.</summary>
    public async Task NotifyAsync(
        Guid ruleId, string subject, string who, CancellationToken cancellationToken = default)
    {
        var (rule, trigger) = await RuleAndTrigger(ruleId, cancellationToken);

        Refuse(string.IsNullOrWhiteSpace(subject) ? "Say what the notice should tell them." : null);
        Refuse(Wording.Refusal(subject, trigger));
        Refuse(Who.Refusal(trigger, who, roles: true, required: true));
        await CheckPersonAsync(who, cancellationToken);

        rule.Then(AutomationActionKind.Notify, subject, who: who);
        await rules.SaveAsync(cancellationToken);
    }

    /// <summary>THEN email people who work here.</summary>
    public async Task EmailAsync(
        Guid ruleId, string subject, string body, string who,
        CancellationToken cancellationToken = default)
    {
        var (rule, trigger) = await RuleAndTrigger(ruleId, cancellationToken);

        Refuse(string.IsNullOrWhiteSpace(subject) ? "Give the email a subject." : null);
        Refuse(string.IsNullOrWhiteSpace(body) ? "Say something in the email." : null);
        Refuse(Wording.Refusal(subject, trigger));
        Refuse(Wording.Refusal(body, trigger));
        Refuse(Who.Refusal(trigger, who, roles: true, required: true));
        await CheckPersonAsync(who, cancellationToken);

        rule.Then(AutomationActionKind.Email, subject, body, who);
        await rules.SaveAsync(cancellationToken);
    }

    /// <summary>THEN add lines to the joining checklist of the person the event names.</summary>
    public async Task ChecklistAsync(
        Guid ruleId, string lines, string who, CancellationToken cancellationToken = default)
    {
        var (rule, trigger) = await RuleAndTrigger(ruleId, cancellationToken);
        var said = Wording.Lines(lines);

        Refuse(said.Count == 0 ? "Write at least one line, one to a line." : null);
        Refuse(said.Count > 20
            ? "Twenty lines at most. A checklist longer than that is not read to the end."
            : null);
        Refuse(said.Any(line => line.Length > 200) ? "Each line can be at most 200 characters." : null);
        Refuse(Wording.Refusal(lines, trigger));
        Refuse(Who.Parse(who).Kind == WhoKind.Field && trigger.People.Contains(Who.Parse(who).Argument)
            ? null
            : "A checklist belongs to the person the event is about. Choose them.");

        rule.Then(AutomationActionKind.OnboardingSteps, null, string.Join('\n', said), who);
        await rules.SaveAsync(cancellationToken);
    }

    /// <summary>THEN tell one of the outgoing webhook subscriptions.</summary>
    public async Task WebhookAsync(
        Guid ruleId, Guid subscriptionId, CancellationToken cancellationToken = default)
    {
        var rule = await Required(ruleId, cancellationToken);

        Refuse(await rules.SubscriptionExistsAsync(subscriptionId, cancellationToken)
            ? null
            : "Choose one of the outgoing webhook subscriptions. A rule cannot send to an "
                + "address that has not been set up — and signed — on the webhooks page.");

        rule.Then(AutomationActionKind.Webhook, null, subscriptionId: subscriptionId);
        await rules.SaveAsync(cancellationToken);
    }

    public async Task DropActionAsync(
        Guid ruleId, Guid actionId, CancellationToken cancellationToken = default)
    {
        var rule = await Required(ruleId, cancellationToken);

        rule.DropAction(actionId);
        await rules.SaveAsync(cancellationToken);
    }

    public async Task SwitchOnAsync(Guid ruleId, CancellationToken cancellationToken = default)
    {
        var rule = await Required(ruleId, cancellationToken);

        rule.SwitchOn(clock.Now);
        await rules.SaveAsync(cancellationToken);
    }

    public async Task SwitchOffAsync(Guid ruleId, CancellationToken cancellationToken = default)
    {
        var rule = await Required(ruleId, cancellationToken);

        rule.SwitchOff();
        await rules.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Take a rule away altogether — only one that has never fired.
    /// </summary>
    /// <remarks>
    /// A rule with a history is switched off rather than deleted, because its runs say what it
    /// did, and a history pointing at a rule nobody can open any more is a set of unexplained
    /// work items on the board. The shipped templates are never deleted either: starting the
    /// application would put them straight back, which would look like deleting did not work.
    /// </remarks>
    public async Task DeleteAsync(Guid ruleId, CancellationToken cancellationToken = default)
    {
        var rule = await Required(ruleId, cancellationToken);

        if (rule.IsTemplate)
        {
            throw new InvalidOperationException(
                "This rule is one the application ships with. Switch it off instead — it would "
                + "only come back the next time the application starts.");
        }

        if (await rules.HasRunAsync(ruleId, cancellationToken))
        {
            throw new InvalidOperationException(
                "This rule has fired, and its history says what it did. Switch it off instead, "
                + "so that history still has a rule to belong to.");
        }

        rules.Remove(rule);
        await rules.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Put the shipped rules in the database, switched off, if they are not there already.
    /// </summary>
    /// <remarks>
    /// Sections 64 to 66, and the overdue invoice from section 31. Run on every start and
    /// adding only what is missing, so a template added in a release appears after the deploy,
    /// and a template somebody has edited is never overwritten — once it is in the database it
    /// is theirs.
    /// </remarks>
    public async Task<int> SeedTemplatesAsync(CancellationToken cancellationToken = default)
    {
        var added = 0;

        foreach (var template in Templates.All)
        {
            if (await rules.TemplateExistsAsync(template.Key, cancellationToken))
            {
                continue;
            }

            rules.Add(template.Build(clock.Now));
            added++;
        }

        if (added > 0)
        {
            await rules.SaveAsync(cancellationToken);
        }

        return added;
    }

    private async Task<AutomationRule> Required(Guid ruleId, CancellationToken cancellationToken) =>
        await rules.FindAsync(ruleId, cancellationToken)
        ?? throw new InvalidOperationException("That rule does not exist.");

    private async Task<(AutomationRule Rule, Trigger Trigger)> RuleAndTrigger(
        Guid ruleId, CancellationToken cancellationToken)
    {
        var rule = await Required(ruleId, cancellationToken);

        return (rule, Triggers.Required(rule.Trigger));
    }

    private async Task CheckPersonAsync(string? who, CancellationToken cancellationToken)
    {
        if (Who.Parse(who) is (WhoKind.Person, var argument)
            && (!Guid.TryParse(argument, out var id)
                || !await rules.PersonExistsAsync(id, cancellationToken)))
        {
            throw new InvalidOperationException("That person is not on the staff list.");
        }
    }

    private async Task CheckProjectAsync(
        Trigger trigger, string? where, CancellationToken cancellationToken)
    {
        switch (Who.Parse(where))
        {
            case (WhoKind.None, _):
                return;
            case (WhoKind.Field, var field) when trigger.Project == field:
                return;
            case (WhoKind.Project, var argument)
                when Guid.TryParse(argument, out var id)
                    && await rules.ProjectExistsAsync(id, cancellationToken):
                return;
            default:
                throw new InvalidOperationException(
                    "Choose a project from the list, or the project the event names.");
        }
    }

    private static void Refuse(string? refusal)
    {
        if (refusal is not null)
        {
            throw new InvalidOperationException(refusal);
        }
    }
}
