using System.Text.RegularExpressions;
using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Domain.Automation;

namespace JiranisokoTech.Application.Automation;

/// <summary>
/// Text a rule writes, with the event's fields put in: "Create accounts for {FullName}".
/// </summary>
/// <remarks>
/// A substitution and nothing more. A name in braces is replaced by that field of the event
/// written as words, and a name the trigger does not carry is refused when the rule is saved —
/// so a title cannot reach the board reading "Welcome {FulName}", and there is no syntax in
/// which anything could be evaluated. What is substituted is data somebody typed elsewhere in
/// the application, and it is escaped where it lands the way every other value is: Razor on
/// the pages, <c>Letters</c> in the emails.
/// </remarks>
public static partial class Wording
{
    /// <summary>The rule's own name, available in any text it writes.</summary>
    public const string RuleName = "Rule";

    public static string Fill(string template, Facts facts, string ruleName) =>
        Placeholder().Replace(template, match =>
        {
            var name = match.Groups[1].Value;

            if (string.Equals(name, RuleName, StringComparison.OrdinalIgnoreCase))
            {
                return ruleName;
            }

            return facts.Trigger.Field(name) is null ? match.Value : facts.Said(name) ?? "—";
        });

    /// <summary>
    /// The link to the event's subject, or null when the field it needs is empty.
    /// </summary>
    /// <remarks>
    /// A pull request with no work item named has no page to go to, and a link to
    /// "/work/" is worse than none.
    /// </remarks>
    public static string? Link(Facts facts)
    {
        if (facts.Trigger.Link is not { } template)
        {
            return null;
        }

        var missing = false;

        var link = Placeholder().Replace(template, match =>
        {
            var said = facts.Said(match.Groups[1].Value);

            if (string.IsNullOrWhiteSpace(said) || said == Guid.Empty.ToString())
            {
                missing = true;
            }

            return said ?? string.Empty;
        });

        return missing ? null : link;
    }

    /// <summary>The names in braces that this trigger does not carry.</summary>
    public static IReadOnlyList<string> Unknown(string? template, Trigger trigger) =>
        template is null
            ? []
            : [.. Placeholder().Matches(template)
                .Select(match => match.Groups[1].Value)
                .Where(name => !string.Equals(name, RuleName, StringComparison.OrdinalIgnoreCase)
                    && trigger.Field(name) is null)
                .Distinct(StringComparer.Ordinal)];

    public static string? Refusal(string? template, Trigger trigger) =>
        Unknown(template, trigger) is { Count: > 0 } unknown
            ? $"{string.Join(", ", unknown.Select(one => "{" + one + "}"))} "
                + $"{(unknown.Count == 1 ? "is" : "are")} not something "
                + $"{trigger.Label.ToLowerInvariant()} carries. It carries: "
                + string.Join(", ", trigger.Fields.Select(one => "{" + one.Name + "}"))
                + $" and {{{RuleName}}}."
            : null;

    /// <summary>The action in words, for the rule page and for the run history.</summary>
    public static string Describe(Trigger trigger, AutomationAction action) => action.Kind switch
    {
        AutomationActionKind.RaiseWork =>
            $"Raise work “{action.Text}”"
            + Where(trigger, action.Where)
            + (action.Who is null ? string.Empty : $", for {Who.Describe(trigger, action.Who)}")
            + (action.Days is { } days ? $", due {(days == 0 ? "the same day" : $"in {days} day(s)")}" : string.Empty),
        AutomationActionKind.Notify =>
            $"Tell {Who.Describe(trigger, action.Who)}: “{action.Text}”",
        AutomationActionKind.Email =>
            $"Email {Who.Describe(trigger, action.Who)}: “{action.Text}”",
        AutomationActionKind.OnboardingSteps =>
            $"Add {Lines(action.Body).Count} line(s) to the joining checklist of "
            + Who.Describe(trigger, action.Who),
        AutomationActionKind.Webhook => "Notify an outgoing webhook subscription",
        _ => action.Kind.ToString(),
    };

    /// <summary>A text box's lines, blank ones dropped.</summary>
    public static IReadOnlyList<string> Lines(string? text) =>
        text is null
            ? []
            : [.. text.Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)];

    private static string Where(Trigger trigger, string? where) =>
        Who.Parse(where) switch
        {
            (WhoKind.Field, var field) =>
                $" on the {trigger.Field(field)?.Label.ToLowerInvariant() ?? field} it names",
            (WhoKind.Project, _) => " on a chosen project",
            _ => string.Empty,
        };

    [GeneratedRegex(@"\{([A-Za-z][A-Za-z0-9]*)\}")]
    private static partial Regex Placeholder();
}

/// <summary>The ways a rule can say who something is for.</summary>
public enum WhoKind
{
    None,

    /// <summary>The person, or project, named in a field of the event.</summary>
    Field,

    /// <summary>
    /// The manager of the person named in a field — or, when nobody is named as their manager
    /// yet, the head of their department.
    /// </summary>
    Manager,

    /// <summary>The lead of the project named in a field.</summary>
    Lead,

    /// <summary>The head of the department named in a field.</summary>
    Head,

    /// <summary>Everybody who works here and signs in with a role.</summary>
    Role,

    /// <summary>One named person.</summary>
    Person,

    /// <summary>One chosen project, for where work goes.</summary>
    Project,
}

/// <summary>
/// "Who" and "where", written as short strings a select can carry: <c>field:EmployeeId</c>,
/// <c>manager:EmployeeId</c>, <c>role:hr</c>, <c>person:0199…</c>, <c>project:0199…</c>.
/// </summary>
/// <remarks>
/// A string rather than three columns, because it is chosen from one list on the page and read
/// in one place here, and every one of them names either a field of the trigger, a role in the
/// matrix or a record by its identifier — all checked when the rule is saved.
///
/// There is no way to name an email address. Every message a rule sends goes to somebody who
/// works here, at the address they sign in with: a rule is written once and fires for months,
/// and one that could write to any address would be a standing way to send the firm's records
/// out of it that no other permission governs.
/// </remarks>
public static class Who
{
    /// <summary>The most people one action may reach.</summary>
    /// <remarks>
    /// A bound on the fan-out a role can cause. "Tell every developer" is reasonable in a firm of
    /// thirty; in a firm of three hundred it is an announcement, and there is a page for those.
    /// </remarks>
    public const int MostPeople = 25;

    public static (WhoKind Kind, string Argument) Parse(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec) || !spec.Contains(':'))
        {
            return (WhoKind.None, string.Empty);
        }

        var at = spec.IndexOf(':');
        var kind = spec[..at].Trim().ToLowerInvariant() switch
        {
            "field" => WhoKind.Field,
            "manager" => WhoKind.Manager,
            "lead" => WhoKind.Lead,
            "head" => WhoKind.Head,
            "role" => WhoKind.Role,
            "person" => WhoKind.Person,
            "project" => WhoKind.Project,
            _ => WhoKind.None,
        };

        return (kind, spec[(at + 1)..].Trim());
    }

    /// <summary>
    /// The choices of who a rule on this trigger can reach, as (value, words).
    /// </summary>
    /// <param name="roles">Whether a whole role may be chosen — for telling, not for giving work to.</param>
    public static IEnumerable<(string Value, string Said)> Choices(Trigger trigger, bool roles)
    {
        foreach (var field in trigger.People)
        {
            var label = trigger.Field(field)?.Label.ToLowerInvariant() ?? field;

            yield return ($"field:{field}", $"The {label} it names");
            yield return ($"manager:{field}", $"The manager of the {label} it names");
        }

        if (trigger.Project is { } project)
        {
            yield return ($"lead:{project}", "The lead of the project it names");
        }

        if (trigger.Department is { } department)
        {
            yield return ($"head:{department}", "The head of the department it names");
        }

        if (!roles)
        {
            yield break;
        }

        foreach (var role in Roles.All)
        {
            yield return ($"role:{role}", $"Everybody who is {RoleWords(role)}");
        }
    }

    /// <summary>Why this "who" cannot be used on this trigger, or null when it can.</summary>
    public static string? Refusal(Trigger trigger, string? spec, bool roles, bool required)
    {
        var (kind, argument) = Parse(spec);

        return kind switch
        {
            WhoKind.None when required => "Say who this is for.",
            WhoKind.None => null,
            WhoKind.Field or WhoKind.Manager when !trigger.People.Contains(argument) =>
                $"{trigger.Label} does not name a person in \"{argument}\".",
            WhoKind.Lead when trigger.Project != argument =>
                $"{trigger.Label} does not name a project in \"{argument}\".",
            WhoKind.Head when trigger.Department != argument =>
                $"{trigger.Label} does not name a department in \"{argument}\".",
            WhoKind.Role when !roles =>
                "Work is given to one person, not to a role. Choose a person, or tell the role "
                + "instead.",
            WhoKind.Role when !Roles.All.Contains(argument) => $"There is no role called \"{argument}\".",
            WhoKind.Person when !Guid.TryParse(argument, out _) => "Choose somebody from the list.",
            WhoKind.Project => "That is a project, not a person.",
            _ => null,
        };
    }

    public static string Describe(Trigger trigger, string? spec)
    {
        var (kind, argument) = Parse(spec);
        var label = trigger.Field(argument)?.Label.ToLowerInvariant() ?? argument;

        return kind switch
        {
            WhoKind.Field => $"the {label} it names",
            WhoKind.Manager => $"the manager of the {label} it names",
            WhoKind.Lead => "the project's lead",
            WhoKind.Head => "the head of the department",
            WhoKind.Role => $"everybody who is {RoleWords(argument)}",
            WhoKind.Person => "a named person",
            _ => "nobody",
        };
    }

    public static string RoleWords(string role) => role switch
    {
        Roles.HumanResources => "in HR",
        Roles.DevOpsEngineer => "a DevOps engineer (IT)",
        Roles.Owner => "an owner",
        Roles.Administrator => "an administrator",
        Roles.Auditor => "an auditor",
        Roles.Accountant => "an accountant",
        Roles.EngineeringManager => "an engineering manager",
        Roles.Interviewer => "an interviewer",
        Roles.Sales => "in sales",
        Roles.Support => "in support",
        Roles.QaEngineer => "a QA engineer",
        _ => "a " + role.Replace('_', ' '),
    };
}
