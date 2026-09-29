using System.Globalization;
using JiranisokoTech.Domain.Automation;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Application.Automation;

/// <summary>
/// An event's fields, by name, as values a condition can compare.
/// </summary>
/// <remarks>
/// Read off the event record by reflection over the fields the trigger lists, and nothing
/// else — a condition names a field, and the only fields it can name are the ones the trigger
/// offered when the rule was written.
/// </remarks>
public sealed class Facts
{
    private readonly Dictionary<string, object?> _values;

    private Facts(Trigger trigger, Dictionary<string, object?> values)
    {
        Trigger = trigger;
        _values = values;
    }

    public Trigger Trigger { get; }

    public static Facts Of(Trigger trigger, IDomainEvent domainEvent)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var type = domainEvent.GetType();

        foreach (var field in trigger.Fields)
        {
            values[field.Name] = type.GetProperty(field.Name)?.GetValue(domainEvent);
        }

        return new Facts(trigger, values);
    }

    public object? this[string field] => _values.GetValueOrDefault(field);

    public Guid? Reference(string field) => this[field] switch
    {
        Guid id when id != Guid.Empty => id,
        _ => null,
    };

    /// <summary>A field as words, for a work item title or an email.</summary>
    public string? Said(string field) => this[field] switch
    {
        null => null,
        string text => text,
        DateOnly on => on.ToString("d MMM yyyy", CultureInfo.InvariantCulture),
        DateTimeOffset at => at.ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture),
        bool flag => flag ? "yes" : "no",
        Enum choice => Labels.Words(choice.ToString()),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        var other => other.ToString(),
    };
}

/// <summary>
/// The IF of a rule: what a condition may say, and whether it holds.
/// </summary>
/// <remarks>
/// Every comparison is typed. The value somebody typed is read as a number, a date, a yes or
/// no, one of an enumeration's names or an identifier, according to the field it is compared
/// with — and it is read when the rule is saved, so a condition that could never be read is
/// refused on the screen instead of being false, silently, for every event that ever arrives.
/// There is no expression language and nothing is evaluated; the whole of what a condition can
/// do is the switch in <see cref="Holds"/>.
/// </remarks>
public static class Conditions
{
    /// <summary>Which comparisons make sense for which kind of field.</summary>
    public static IReadOnlyList<ConditionOperator> For(FieldKind kind) => kind switch
    {
        FieldKind.Text =>
        [
            ConditionOperator.Is, ConditionOperator.IsNot, ConditionOperator.Contains,
            ConditionOperator.DoesNotContain, ConditionOperator.IsSet, ConditionOperator.IsNotSet,
        ],
        FieldKind.Number or FieldKind.Date =>
        [
            ConditionOperator.Is, ConditionOperator.IsNot, ConditionOperator.AtLeast,
            ConditionOperator.AtMost, ConditionOperator.IsSet, ConditionOperator.IsNotSet,
        ],
        FieldKind.Choice => [ConditionOperator.Is, ConditionOperator.IsNot],
        FieldKind.YesNo => [ConditionOperator.Is],
        FieldKind.Reference =>
        [
            ConditionOperator.Is, ConditionOperator.IsNot, ConditionOperator.IsSet,
            ConditionOperator.IsNotSet,
        ],
        _ => [],
    };

    public static bool NeedsValue(ConditionOperator comparison) =>
        comparison is not (ConditionOperator.IsSet or ConditionOperator.IsNotSet);

    /// <summary>
    /// Why this condition cannot be written against this trigger, or null when it can.
    /// </summary>
    public static string? Refusal(
        Trigger trigger, string field, ConditionOperator comparison, string? value)
    {
        if (trigger.Field(field) is not { } known || known.Kind == FieldKind.Moment)
        {
            return $"\"{field}\" is not something {trigger.Label.ToLowerInvariant()} carries. "
                + "Choose one of the fields listed.";
        }

        if (!For(known.Kind).Contains(comparison))
        {
            return $"{known.Label} cannot be compared that way. It takes: "
                + string.Join(", ", For(known.Kind).Select(Said)) + ".";
        }

        if (!NeedsValue(comparison))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return $"Say what {known.Label.ToLowerInvariant()} should be compared with.";
        }

        return Read(known, value) is null
            ? known.Kind switch
            {
                FieldKind.Number => $"{known.Label} is a number, and \"{value}\" is not one.",
                FieldKind.Date => $"{known.Label} is a date. Write it as 2026-10-01.",
                FieldKind.Choice => $"{known.Label} is one of: "
                    + string.Join(", ", known.Choices) + ".",
                FieldKind.YesNo => $"{known.Label} is yes or no.",
                FieldKind.Reference => $"{known.Label} is an identifier, such as the one at the "
                    + "end of a record's address.",
                _ => $"\"{value}\" cannot be read as {known.Label.ToLowerInvariant()}.",
            }
            : null;
    }

    /// <summary>Whether every condition on the rule holds for this event.</summary>
    /// <remarks>
    /// All of them, joined by AND. The brief's examples need nothing else, and a rule that
    /// needs an OR is two rules — which is also easier to read in the history, because the
    /// history then says which of the two fired.
    /// </remarks>
    public static bool AllHold(IEnumerable<AutomationCondition> conditions, Facts facts) =>
        conditions.All(condition => Holds(condition, facts));

    public static bool Holds(AutomationCondition condition, Facts facts)
    {
        // A field the event no longer carries holds nothing. Refusing to fire is the safe
        // reading of a rule that no longer makes sense.
        if (facts.Trigger.Field(condition.Field) is not { } field)
        {
            return false;
        }

        var actual = facts[field.Name];
        var set = actual switch
        {
            null => false,
            string text => !string.IsNullOrWhiteSpace(text),
            Guid id => id != Guid.Empty,
            _ => true,
        };

        switch (condition.Operator)
        {
            case ConditionOperator.IsSet:
                return set;
            case ConditionOperator.IsNotSet:
                return !set;
        }

        var expected = Read(field, condition.Value ?? string.Empty);

        if (expected is null)
        {
            return false;
        }

        if (!set)
        {
            // Nothing is equal to a value, less than it or containing it; it is "not" it.
            return condition.Operator is ConditionOperator.IsNot or ConditionOperator.DoesNotContain;
        }

        return field.Kind switch
        {
            FieldKind.Text => CompareText(condition.Operator, (string)actual!, (string)expected),
            FieldKind.Choice => CompareEqual(
                condition.Operator,
                string.Equals(actual!.ToString(), (string)expected, StringComparison.OrdinalIgnoreCase)),
            FieldKind.Reference => CompareEqual(condition.Operator, (Guid)actual! == (Guid)expected),
            FieldKind.YesNo => CompareEqual(condition.Operator, (bool)actual! == (bool)expected),
            FieldKind.Number => CompareOrdered(
                condition.Operator,
                Convert.ToDecimal(actual, CultureInfo.InvariantCulture).CompareTo((decimal)expected)),
            FieldKind.Date => CompareOrdered(
                condition.Operator, ((DateOnly)actual!).CompareTo((DateOnly)expected)),
            _ => false,
        };
    }

    public static string Said(ConditionOperator comparison) => comparison switch
    {
        ConditionOperator.Is => "is",
        ConditionOperator.IsNot => "is not",
        ConditionOperator.Contains => "contains",
        ConditionOperator.DoesNotContain => "does not contain",
        ConditionOperator.AtLeast => "is at least",
        ConditionOperator.AtMost => "is at most",
        ConditionOperator.IsSet => "is filled in",
        _ => "is empty",
    };

    /// <summary>The condition in words, as the rule page and the audit trail's reader see it.</summary>
    public static string Describe(Trigger trigger, AutomationCondition condition)
    {
        var label = trigger.Field(condition.Field)?.Label ?? condition.Field;

        return NeedsValue(condition.Operator)
            ? $"{label} {Said(condition.Operator)} \"{condition.Value}\""
            : $"{label} {Said(condition.Operator)}";
    }

    /// <summary>The typed value, or null when the text cannot be read as the field's kind.</summary>
    private static object? Read(TriggerField field, string value)
    {
        var text = value.Trim();

        return field.Kind switch
        {
            FieldKind.Text => text,
            FieldKind.Number => decimal.TryParse(
                text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
                ? number
                : null,
            FieldKind.Date => DateOnly.TryParseExact(
                text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var on)
                ? on
                : null,
            FieldKind.Choice => field.Choices.FirstOrDefault(
                choice => string.Equals(choice, text, StringComparison.OrdinalIgnoreCase)),
            FieldKind.YesNo => text.ToLowerInvariant() switch
            {
                "yes" or "true" => true,
                "no" or "false" => false,
                _ => null,
            },
            FieldKind.Reference => Guid.TryParse(text, out var id) ? id : null,
            _ => null,
        };
    }

    private static bool CompareText(ConditionOperator comparison, string actual, string expected) =>
        comparison switch
        {
            ConditionOperator.Is => string.Equals(
                actual.Trim(), expected, StringComparison.OrdinalIgnoreCase),
            ConditionOperator.IsNot => !string.Equals(
                actual.Trim(), expected, StringComparison.OrdinalIgnoreCase),
            ConditionOperator.Contains => actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
            ConditionOperator.DoesNotContain =>
                !actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    private static bool CompareEqual(ConditionOperator comparison, bool equal) =>
        comparison switch
        {
            ConditionOperator.Is => equal,
            ConditionOperator.IsNot => !equal,
            _ => false,
        };

    private static bool CompareOrdered(ConditionOperator comparison, int order) =>
        comparison switch
        {
            ConditionOperator.Is => order == 0,
            ConditionOperator.IsNot => order != 0,
            ConditionOperator.AtLeast => order >= 0,
            ConditionOperator.AtMost => order <= 0,
            _ => false,
        };
}
