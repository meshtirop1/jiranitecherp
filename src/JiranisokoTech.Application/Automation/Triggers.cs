using System.Globalization;
using System.Reflection;
using JiranisokoTech.Domain.Clients;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Domain.Contracts;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Domain.Incidents;
using JiranisokoTech.Domain.Money;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Recruitment;
using JiranisokoTech.Domain.Time;
using JiranisokoTech.Domain.Work;

namespace JiranisokoTech.Application.Automation;

/// <summary>What kind of value a field holds, which decides the comparisons it allows.</summary>
public enum FieldKind
{
    Text,
    Number,
    Date,

    /// <summary>A moment in time. Offered for wording, not for conditions — see <see cref="Conditions"/>.</summary>
    Moment,

    /// <summary>One of a closed list of names, such as a status or a severity.</summary>
    Choice,

    YesNo,

    /// <summary>An identifier of something else in the system.</summary>
    Reference,
}

/// <summary>One field of an event, as a rule sees it.</summary>
public sealed record TriggerField(string Name, string Label, FieldKind Kind, IReadOnlyList<string> Choices);

/// <summary>
/// An event a rule may start from, and what a rule may do with its fields.
/// </summary>
/// <param name="Name">The event's type name — what the outbox stores and a rule names.</param>
/// <param name="Summary">One line about the event for the run history, in <c>{Field}</c> wording.</param>
/// <param name="Link">Where in the application the event's subject lives, in the same wording.</param>
/// <param name="People">Fields naming somebody on the staff list, who can be told or given work.</param>
/// <param name="Project">A field naming a project, which work can be raised on.</param>
/// <param name="Department">A field naming a department, whose head can be told.</param>
public sealed record Trigger(
    string Name,
    Type Event,
    string Area,
    string Label,
    string Summary,
    string? Link,
    IReadOnlyList<string> People,
    string? Project = null,
    string? Department = null)
{
    public IReadOnlyList<TriggerField> Fields { get; } = FieldsOf(Event);

    public TriggerField? Field(string name) =>
        Fields.FirstOrDefault(one => string.Equals(one.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The fields a condition may test: everything but moments in time.</summary>
    public IEnumerable<TriggerField> Testable => Fields.Where(one => one.Kind != FieldKind.Moment);

    /// <summary>
    /// The event's fields, read off the record itself.
    /// </summary>
    /// <remarks>
    /// Read rather than listed, so that a field added to an event is offered to rules on the
    /// next release without anybody remembering to add it here — and one removed stops being
    /// offered, rather than lingering as a condition that tests a field that no longer exists.
    /// When the event happened is left out: every event has it, it is the same moment the
    /// run history already shows, and a condition on it could only ever compare it with a
    /// date somebody typed long ago.
    /// </remarks>
    private static List<TriggerField> FieldsOf(Type type) =>
        [.. type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.Name != nameof(IDomainEvent.OccurredAt)
                && property.GetIndexParameters().Length == 0)
            .Select(property => KindOf(property.PropertyType) is { } kind
                ? new TriggerField(
                    property.Name,
                    Labels.For(property.Name),
                    kind,
                    Choices(property.PropertyType))
                : null)
            .OfType<TriggerField>()];

    private static FieldKind? KindOf(Type type)
    {
        var bare = Nullable.GetUnderlyingType(type) ?? type;

        if (bare.IsEnum)
        {
            return FieldKind.Choice;
        }

        return bare switch
        {
            _ when bare == typeof(string) => FieldKind.Text,
            _ when bare == typeof(int) || bare == typeof(long) || bare == typeof(decimal) => FieldKind.Number,
            _ when bare == typeof(DateOnly) => FieldKind.Date,
            _ when bare == typeof(DateTimeOffset) => FieldKind.Moment,
            _ when bare == typeof(bool) => FieldKind.YesNo,
            _ when bare == typeof(Guid) => FieldKind.Reference,

            // Lists and anything else a rule could not compare with a typed value.
            _ => null,
        };
    }

    private static IReadOnlyList<string> Choices(Type type)
    {
        var bare = Nullable.GetUnderlyingType(type) ?? type;

        return bare.IsEnum ? Enum.GetNames(bare) : [];
    }
}

/// <summary>
/// The events a rule may start from.
/// </summary>
/// <remarks>
/// A list chosen here rather than every event in the domain, for the same reason the outgoing
/// webhooks have one: an event is offered to rules by a decision, not by existing. Some events
/// are plumbing (a delivery received, a key used) and a rule on them would be noise; some carry
/// what should not flow into a work item title or an email (pay terms, where somebody signed in
/// from). Neither is here.
///
/// What is here is the brief's own list from section 30 where this application raises the
/// event, and the three the section 64 to 66 automations start from. Adding one is a line in
/// this file and nothing else — the handler that matches rules is registered for every entry.
/// </remarks>
public static class Triggers
{
    public static IReadOnlyList<Trigger> All { get; } =
    [
        new(nameof(WorkItemRaised), typeof(WorkItemRaised), "Work",
            "A piece of work is raised", "{Title}", "/work/{WorkItemId}",
            [nameof(WorkItemRaised.RaisedById)], Project: nameof(WorkItemRaised.ProjectId)),

        new(nameof(WorkItemMoved), typeof(WorkItemMoved), "Work",
            "A piece of work moves on the board", "Moved from {From} to {To}", "/work/{WorkItemId}",
            []),

        new(nameof(WorkItemAssigned), typeof(WorkItemAssigned), "Work",
            "A piece of work is given to somebody", "{Title}", "/work/{WorkItemId}",
            [nameof(WorkItemAssigned.ToEmployeeId), nameof(WorkItemAssigned.FromEmployeeId)]),

        new(nameof(WorkItemCompleted), typeof(WorkItemCompleted), "Work",
            "A piece of work is finished", "{Title}", "/work/{WorkItemId}",
            [nameof(WorkItemCompleted.AssigneeId)], Project: nameof(WorkItemCompleted.ProjectId)),

        new(nameof(ProjectStarted), typeof(ProjectStarted), "Work",
            "A project is started", "{Name} ({Code})", "/projects/{ProjectId}",
            [], Project: nameof(ProjectStarted.ProjectId)),

        new(nameof(ProjectStatusChanged), typeof(ProjectStatusChanged), "Work",
            "A project is held, delivered, cancelled or resumed", "{Name} is now {To}",
            "/projects/{ProjectId}",
            [], Project: nameof(ProjectStatusChanged.ProjectId)),

        new(nameof(PullRequestOpened), typeof(PullRequestOpened), "Engineering",
            "A pull request is opened", "#{Number} {Title}", "/work/{WorkItemId}",
            []),

        new(nameof(PullRequestMerged), typeof(PullRequestMerged), "Engineering",
            "A pull request is merged", "#{Number} into {Branch}", "/work/{WorkItemId}",
            []),

        new(nameof(BuildFinished), typeof(BuildFinished), "Engineering",
            "A build finishes", "{Branch}: {Outcome}", "/work/{WorkItemId}",
            []),

        new(nameof(DeploymentFinished), typeof(DeploymentFinished), "Engineering",
            "A deployment finishes", "{EnvironmentName}: {State}", "/work/{WorkItemId}",
            []),

        new(nameof(ReleaseDeclared), typeof(ReleaseDeclared), "Engineering",
            "A release is declared", "Version {Version}", "/releases/{ReleaseId}",
            []),

        new(nameof(IncidentRaised), typeof(IncidentRaised), "Incidents",
            "An incident is raised", "#{Number} {Title} ({Severity})", "/incidents/{Number}",
            []),

        new(nameof(IncidentResolved), typeof(IncidentResolved), "Incidents",
            "An incident is resolved", "#{Number} ({Severity})", "/incidents/{Number}",
            []),

        new(nameof(EmployeeHired), typeof(EmployeeHired), "People",
            "Somebody is hired", "{FullName}, starting {StartsOn}", "/people/{EmployeeId}",
            [nameof(EmployeeHired.EmployeeId)], Department: nameof(EmployeeHired.DepartmentId)),

        new(nameof(EmployeeStarted), typeof(EmployeeStarted), "People",
            "Somebody starts work", "{FullName}", "/people/{EmployeeId}",
            [nameof(EmployeeStarted.EmployeeId)]),

        new(nameof(OffboardingStarted), typeof(OffboardingStarted), "People",
            "Somebody's leaving is begun", "Leaving on {LeavingOn}", "/people/{EmployeeId}",
            [nameof(OffboardingStarted.EmployeeId)]),

        new(nameof(EmployeeLeft), typeof(EmployeeLeft), "People",
            "Somebody leaves", "{FullName}, on {On}", "/people/{EmployeeId}",
            [nameof(EmployeeLeft.EmployeeId)]),

        new(nameof(LeaveApproved), typeof(LeaveApproved), "People",
            "Leave is approved", "{Kind} leave, {From} to {To}", null,
            [nameof(LeaveApproved.EmployeeId)]),

        new(nameof(OfferAccepted), typeof(OfferAccepted), "Hiring",
            "An offer is accepted", "{JobTitle}, starting {StartsOn}", null,
            []),

        new(nameof(ClientTakenOn), typeof(ClientTakenOn), "Clients",
            "A client is taken on", "{Name} ({Code})", "/clients/{ClientId}",
            []),

        new(nameof(ClientStatusChanged), typeof(ClientStatusChanged), "Clients",
            "A client's standing changes", "{Name}: {From} to {To}", "/clients/{ClientId}",
            []),

        new(nameof(OpportunityClosed), typeof(OpportunityClosed), "Clients",
            "An opportunity is won or lost", "{Title}: {Stage}", "/clients/{ClientId}",
            []),

        new(nameof(ContractActivated), typeof(ContractActivated), "Clients",
            "A contract comes into force", "{Reference}, {StartsOn} to {EndsOn}",
            "/contracts/{ContractId}",
            []),

        new(nameof(InvoiceSent), typeof(InvoiceSent), "Money",
            "An invoice is sent", "{Number}, due {DueOn}", "/invoices/{InvoiceId}",
            []),

        new(nameof(InvoiceOverdue), typeof(InvoiceOverdue), "Money",
            "An invoice is 7, 21 or 45 days overdue (checked daily)",
            "{Number}, {DaysOverdue} days overdue", "/invoices/{InvoiceId}",
            []),

        new(nameof(InvoiceSettled), typeof(InvoiceSettled), "Money",
            "An invoice is paid in full", "{Number}", "/invoices/{InvoiceId}",
            []),

        new(nameof(ExpenseSubmitted), typeof(ExpenseSubmitted), "Money",
            "An expense claim is submitted", "A claim for {MinorUnits} {Currency}", null,
            [nameof(ExpenseSubmitted.EmployeeId)]),
    ];

    public static Trigger? Find(string? name) =>
        All.FirstOrDefault(one => string.Equals(one.Name, name, StringComparison.Ordinal));

    public static Trigger Required(string name) =>
        Find(name) ?? throw new InvalidOperationException(
            $"\"{name}\" is not something a rule can start from.");
}

/// <summary>Field names turned into words a person would use.</summary>
public static class Labels
{
    public static string For(string name)
    {
        /*
         * Money travels in events as a count of minor units, and a label that said "Amount"
         * would invite somebody to write "at least 100000" meaning shillings and get a
         * thousand. Saying so in the label is cheaper than a conversion that would have to
         * guess the currency.
         */
        if (name.EndsWith("MinorUnits", StringComparison.Ordinal))
        {
            var prefix = name[..^"MinorUnits".Length];

            return (prefix.Length == 0 ? "Amount" : For(prefix) + " amount") + " (in cents)";
        }

        var bare = name;

        if (bare.Length > 2 && bare.EndsWith("Id", StringComparison.Ordinal))
        {
            bare = bare[..^2];
        }

        var words = new System.Text.StringBuilder();

        for (var i = 0; i < bare.Length; i++)
        {
            var c = bare[i];

            if (i > 0 && char.IsUpper(c))
            {
                words.Append(' ');
                words.Append(char.ToLowerInvariant(c));
            }
            else
            {
                words.Append(i == 0 ? char.ToUpperInvariant(c) : c);
            }
        }

        return words.ToString();
    }

    /// <summary>An enumeration's name as words: DoneWithRefusals becomes "done with refusals".</summary>
    public static string Words(string name) => For(name).ToLower(CultureInfo.InvariantCulture);
}
