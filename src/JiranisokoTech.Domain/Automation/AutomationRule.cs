using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Automation;

/// <summary>How a condition compares an event's field with the value somebody typed.</summary>
/// <remarks>
/// A closed list rather than an expression. A rule is written by a person on a screen and run
/// by the firm with nobody watching, and the moment a condition can be a piece of text that is
/// evaluated, the screen is a place to write code that runs as the firm. Eight comparisons
/// cover every example the brief gives, and each one is checked against the kind of field it
/// is applied to when the rule is saved rather than when it fires.
/// </remarks>
public enum ConditionOperator
{
    Is = 1,
    IsNot = 2,
    Contains = 3,
    DoesNotContain = 4,

    /// <summary>At least, for a number; on or after, for a date.</summary>
    AtLeast = 5,

    /// <summary>At most, for a number; on or before, for a date.</summary>
    AtMost = 6,

    IsSet = 7,
    IsNotSet = 8,
}

/// <summary>What a rule does when it fires.</summary>
/// <remarks>
/// Each is carried out by a service that already exists — the one a person would reach through
/// the matching page — so a rule cannot do anything the application has no rules for. There is
/// deliberately nothing that deletes, pays, approves or grants access: those are decisions, and
/// a rule is a way of not forgetting to do something, not a way of deciding it.
/// </remarks>
public enum AutomationActionKind
{
    /// <summary>Raise a piece of work, optionally on a project and for somebody.</summary>
    RaiseWork = 1,

    /// <summary>Put a notice in people's notice centre, emailed as their preferences say.</summary>
    Notify = 2,

    /// <summary>Email people who work here, whatever their notice preferences.</summary>
    Email = 3,

    /// <summary>Add lines to a joiner's checklist, starting one if there is none.</summary>
    OnboardingSteps = 4,

    /// <summary>Queue a signed notification to one of the outgoing webhook subscriptions.</summary>
    Webhook = 5,
}

/// <summary>
/// WHEN something happens, IF these hold, THEN do these things.
/// </summary>
/// <remarks>
/// Section 31. A rule is data rather than a class: the fixed handlers in the codebase are how
/// modules react to each other and are written by developers; this is how the firm reacts to
/// its own events in ways it decides for itself, without a release.
///
/// <b>The trigger is the name of a domain event</b>, and only one the application offers as a
/// trigger — the catalogue is in <c>Application.Automation.Triggers</c>. A rule names it by the
/// same plain type name the outbox stores, for the reason the outbox gives: a rule written
/// before a namespace moves must still find its event after it.
///
/// <b>A new rule is off.</b> Switching one on is a separate act, refused until it has something
/// to do, so that half-written rules cannot fire while somebody is still adding their actions.
///
/// Audited, as are its conditions and actions, because a rule acts as the firm. "Who told the
/// system to email every client when an invoice is sent" is exactly the question an audit trail
/// is opened to answer.
/// </remarks>
public sealed class AutomationRule : Entity, IAuditable
{
    /// <summary>The most conditions one rule may carry.</summary>
    public const int MostConditions = 10;

    /// <summary>
    /// The most actions one rule may carry.
    /// </summary>
    /// <remarks>
    /// A bound on fan-out that holds whatever the rule says. One event fanning out into dozens
    /// of pieces of work is almost never what somebody meant, and when it is, two rules say so
    /// more legibly than one.
    /// </remarks>
    public const int MostActions = 10;

    /// <summary>The longest a rule may wait between its event and its actions: thirty days.</summary>
    public const int MostDelayMinutes = 30 * 24 * 60;

    private readonly List<AutomationCondition> _conditions = [];

    private readonly List<AutomationAction> _actions = [];

    private AutomationRule()
    {
        Name = string.Empty;
        Trigger = string.Empty;
    }

    private AutomationRule(
        string name, string trigger, string? description, string? templateKey, DateTimeOffset at)
    {
        Name = Required(name, nameof(name), 200);
        Trigger = Required(trigger, nameof(trigger), 200);
        Description = Optional(description, 2000);
        TemplateKey = Optional(templateKey, 100);
        CreatedAt = at;
    }

    public static AutomationRule Write(
        string name, string trigger, string? description, DateTimeOffset at) =>
        new(name, trigger, description, null, at);

    /// <summary>
    /// One of the rules this application ships with, off until somebody switches it on.
    /// </summary>
    public static AutomationRule FromTemplate(
        string templateKey, string name, string trigger, string description, DateTimeOffset at) =>
        new(name, trigger, description, templateKey, at);

    public string Name { get; private set; }

    public string? Description { get; private set; }

    /// <summary>The event that fires it, by its type name.</summary>
    /// <remarks>
    /// Fixed once written. Every condition and action is phrased in terms of the trigger's
    /// fields, so changing it would leave them all testing fields the new event does not have;
    /// a rule about something else is a new rule.
    /// </remarks>
    public string Trigger { get; private init; }

    public bool IsOn { get; private set; }

    /// <summary>How long to wait after the event before acting. Zero means at once.</summary>
    public int DelayMinutes { get; private set; }

    /// <summary>Which shipped template this came from, if any.</summary>
    /// <remarks>
    /// Kept so that starting the application does not add a template a second time, and so the
    /// page can say where a rule came from. Nullable and unique: two rules from one template
    /// would be two copies of one behaviour, each firing, while a rule somebody wrote from
    /// nothing has no key and many of them may exist — which is exactly what a unique index
    /// over a nullable column enforces.
    /// </remarks>
    public string? TemplateKey { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset? SwitchedOnAt { get; private set; }

    public IReadOnlyList<AutomationCondition> Conditions =>
        [.. _conditions.OrderBy(one => one.Order)];

    public IReadOnlyList<AutomationAction> Actions =>
        [.. _actions.OrderBy(one => one.Order)];

    public bool IsTemplate => TemplateKey is not null;

    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    public void Describe(string name, string? description, int delayMinutes)
    {
        if (delayMinutes is < 0 or > MostDelayMinutes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(delayMinutes),
                "A delay is between nothing and thirty days. A rule that waits longer than a "
                + "month is a reminder, and the firm's reminders are scheduled differently.");
        }

        Name = Required(name, nameof(name), 200);
        Description = Optional(description, 2000);
        DelayMinutes = delayMinutes;
    }

    public AutomationCondition When(string field, ConditionOperator comparison, string? value)
    {
        if (_conditions.Count >= MostConditions)
        {
            throw new InvalidOperationException(
                $"A rule can test at most {MostConditions} things. One that needs more is "
                + "usually two rules.");
        }

        var condition = new AutomationCondition(field, comparison, value, NextOrder(_conditions));

        _conditions.Add(condition);

        return condition;
    }

    public void DropCondition(Guid conditionId) =>
        _conditions.RemoveAll(one => one.Id == conditionId);

    public AutomationAction Then(
        AutomationActionKind kind,
        string? text,
        string? body = null,
        string? who = null,
        string? where = null,
        int? days = null,
        Guid? subscriptionId = null)
    {
        if (_actions.Count >= MostActions)
        {
            throw new InvalidOperationException(
                $"A rule can do at most {MostActions} things when it fires. More than that from "
                + "one event is nearly always a mistake, and when it is not, a second rule says "
                + "so more clearly.");
        }

        var action = new AutomationAction(
            kind, text, body, who, where, days, subscriptionId, NextOrder(_actions));

        _actions.Add(action);

        return action;
    }

    public void DropAction(Guid actionId)
    {
        _actions.RemoveAll(one => one.Id == actionId);

        // A rule left on with nothing to do would record a run for every event and do nothing
        // in each — a history that looks like work and is not.
        if (_actions.Count == 0)
        {
            IsOn = false;
        }
    }

    public void SwitchOn(DateTimeOffset at)
    {
        if (_actions.Count == 0)
        {
            throw new InvalidOperationException(
                "Give the rule something to do first. A rule with no actions would fire, record "
                + "that it fired, and change nothing.");
        }

        if (IsOn)
        {
            return;
        }

        IsOn = true;
        SwitchedOnAt = at;
    }

    public void SwitchOff() => IsOn = false;

    private static int NextOrder<T>(List<T> items)
        where T : IOrdered =>
        items.Count == 0 ? 0 : items.Max(one => one.Order) + 1;

    internal static string Required(string? value, string parameter, int longest)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("This cannot be blank.", parameter);
        }

        var trimmed = value.Trim();

        return trimmed.Length <= longest
            ? trimmed
            : throw new ArgumentException($"This can be at most {longest} characters.", parameter);
    }

    internal static string? Optional(string? value, int longest)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        return trimmed.Length <= longest
            ? trimmed
            : throw new ArgumentException($"This can be at most {longest} characters.");
    }
}

/// <summary>Something with a place in a list.</summary>
public interface IOrdered
{
    int Order { get; }
}

/// <summary>
/// One IF: a field of the event, a comparison, and a value.
/// </summary>
/// <remarks>
/// The value is kept as the text somebody typed, and read as the field's kind when the rule
/// fires — a number, a date, one of an enumeration's names. It is checked against that kind
/// when it is written, so a condition that could never be read is refused on the screen rather
/// than failing silently on every event for a month.
/// </remarks>
public sealed class AutomationCondition : Entity, IAuditable, IOrdered
{
    private AutomationCondition()
    {
        Field = string.Empty;
    }

    internal AutomationCondition(string field, ConditionOperator comparison, string? value, int order)
    {
        if (!Enum.IsDefined(comparison))
        {
            throw new ArgumentOutOfRangeException(nameof(comparison), "That is not a comparison.");
        }

        Field = AutomationRule.Required(field, nameof(field), 100);
        Operator = comparison;
        Value = AutomationRule.Optional(value, 500);
        Order = order;
    }

    public string Field { get; private init; }

    public ConditionOperator Operator { get; private init; }

    public string? Value { get; private init; }

    public int Order { get; private init; }

    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();
}

/// <summary>
/// One THEN.
/// </summary>
/// <remarks>
/// A handful of plain columns whose meaning depends on the kind, rather than a JSON document
/// per kind. Every column is a string a person typed or chose from a list, readable in the
/// table and in the audit trail as it is, and the five kinds between them need six of them:
///
///   Text   — the work's title, the notice's line, the email's subject.
///   Body   — the work's detail, the email's body, the checklist lines one per line.
///   Who    — who it is for: a person, a field of the event, their manager, a role.
///   Where  — the project the work goes on.
///   Days   — when the work is due, counted from the day the rule acts.
///   SubscriptionId — which outgoing webhook to notify.
/// </remarks>
public sealed class AutomationAction : Entity, IAuditable, IOrdered
{
    private AutomationAction()
    {
    }

    internal AutomationAction(
        AutomationActionKind kind,
        string? text,
        string? body,
        string? who,
        string? where,
        int? days,
        Guid? subscriptionId,
        int order)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), "That is not something a rule can do.");
        }

        if (days is < 0 or > 365)
        {
            throw new ArgumentOutOfRangeException(
                nameof(days), "A due date is between today and a year from now.");
        }

        Kind = kind;
        Text = AutomationRule.Optional(text, 300);
        Body = AutomationRule.Optional(body, 4000);
        Who = AutomationRule.Optional(who, 200);
        Where = AutomationRule.Optional(where, 200);
        Days = days;
        SubscriptionId = subscriptionId;
        Order = order;
    }

    public AutomationActionKind Kind { get; private init; }

    public string? Text { get; private init; }

    public string? Body { get; private init; }

    public string? Who { get; private init; }

    public string? Where { get; private init; }

    public int? Days { get; private init; }

    public Guid? SubscriptionId { get; private init; }

    public int Order { get; private init; }

    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();
}
