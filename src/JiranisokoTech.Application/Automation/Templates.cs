using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Domain.Automation;
using JiranisokoTech.Domain.Clients;
using JiranisokoTech.Domain.Money;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Work;

namespace JiranisokoTech.Application.Automation;

/// <summary>A rule the application ships with, switched off.</summary>
public sealed record Template(
    string Key,
    string Name,
    string Trigger,
    string Description,
    Action<AutomationRule> Fill)
{
    public AutomationRule Build(DateTimeOffset at)
    {
        var rule = AutomationRule.FromTemplate(Key, Name, Trigger, Description, at);

        Fill(rule);

        return rule;
    }
}

/// <summary>
/// The automations sections 31 and 64 to 66 describe, as rules somebody can read, change and
/// switch on.
/// </summary>
/// <remarks>
/// Rules rather than handlers, deliberately. A handler that raised setup tasks for every new
/// project would be the firm's process frozen into a release; as a rule it is the firm's
/// process written down where the firm can change it — a step dropped, a title reworded, a
/// different role told — without anybody touching the code.
///
/// Every one of them starts off. A firm that has not yet decided who handles a new client
/// should not start receiving work items about it the day this is deployed.
///
/// <b>What the brief lists that these do not do, and why.</b> Nothing here creates an account,
/// a Git repository, an environment or a folder, or picks a team or a manager. The accounts and
/// the team are decisions — the onboarding service's own remarks explain why access is never
/// granted from a date — so the rules give the decision to a person as a piece of work. A
/// repository cannot be created because no Git provider here holds credentials that can write;
/// environments live at the hosting provider; and documents in this application are attached
/// to their record, so a client's or a project's "folder" is its own page, which exists the
/// moment the record does. The same is true of a client's "CRM record", "workspace" and
/// "billing profile": the client record is all three.
/// </remarks>
public static class Templates
{
    public const string ProjectSetup = "project-setup";

    public const string NewEmployee = "new-employee";

    public const string NewClient = "new-client";

    public const string OverdueInvoice = "overdue-invoice";

    private const string TheProject = "field:" + nameof(ProjectStarted.ProjectId);

    public static IReadOnlyList<Template> All { get; } =
    [
        new(
            ProjectSetup,
            "Set up a new project",
            nameof(ProjectStarted),
            "Section 64. When a project is started, put the standard setup on its board: the "
            + "repository, the first documentation, the team, the environments and the first "
            + "breakdown of the work.",
            rule =>
            {
                rule.Then(AutomationActionKind.RaiseWork,
                    "Create the repository for {Name} and connect it here",
                    "Create it on the firm's Git host under the code {Code}, then connect it on "
                    + "the repositories page so commits and pull requests reach this board.",
                    where: TheProject, days: 3);
                rule.Then(AutomationActionKind.RaiseWork,
                    "Write the README and first architecture notes for {Name}",
                    "What it is for, how to run it, and the decisions already made.",
                    where: TheProject, days: 7);
                rule.Then(AutomationActionKind.RaiseWork,
                    "Agree the team for {Name} and name its lead",
                    where: TheProject, days: 3);
                rule.Then(AutomationActionKind.RaiseWork,
                    "Set up development, staging and production for {Name}",
                    "Record each one on the platform register once it exists.",
                    where: TheProject, days: 10);
                rule.Then(AutomationActionKind.RaiseWork,
                    "Break {Name} into epics and plan the first sprint",
                    where: TheProject, days: 5);
            }),

        new(
            NewEmployee,
            "Get a new joiner ready",
            nameof(EmployeeHired),
            "Section 65. When somebody is hired, give IT the accounts and the equipment to "
            + "prepare, add them to the joiner's checklist, and tell HR, IT and the person's "
            + "manager.",
            rule =>
            {
                const string joiner = "field:" + nameof(EmployeeHired.EmployeeId);
                const string manager = "manager:" + nameof(EmployeeHired.EmployeeId);

                rule.Then(AutomationActionKind.OnboardingSteps, null,
                    "Email account created\nGit host account created and added to the firm\n"
                    + "Chat account created",
                    who: joiner);
                rule.Then(AutomationActionKind.RaiseWork,
                    "Create accounts for {FullName}, who starts on {StartsOn}",
                    "Email, the Git host and chat. Tick each off on their joining checklist.",
                    days: 3);
                rule.Then(AutomationActionKind.RaiseWork,
                    "Prepare equipment for {FullName}, who starts on {StartsOn}",
                    "Issue it from the asset register, which is what ticks it off their checklist.",
                    days: 3);
                rule.Then(AutomationActionKind.RaiseWork,
                    "Put {FullName} on a team and confirm their manager",
                    who: manager, days: 3);
                rule.Then(AutomationActionKind.Notify,
                    "{FullName} has been hired and starts on {StartsOn}.",
                    who: "role:" + Roles.HumanResources);
                rule.Then(AutomationActionKind.Notify,
                    "Accounts and equipment are needed for {FullName}, who starts on {StartsOn}.",
                    who: "role:" + Roles.DevOpsEngineer);
                rule.Then(AutomationActionKind.Notify,
                    "{FullName} joins your team on {StartsOn}.",
                    who: manager);
            }),

        new(
            NewClient,
            "Welcome a new client",
            nameof(ClientTakenOn),
            "Section 66. When a client is taken on, set up the parts of their record that need a "
            + "person — billing, the signed agreement, who to call — and tell sales and finance.",
            rule =>
            {
                rule.Then(AutomationActionKind.RaiseWork,
                    "Confirm billing details for {Name}",
                    "The billing email and payment terms on the client record, before the first "
                    + "invoice goes out.",
                    days: 5);
                rule.Then(AutomationActionKind.RaiseWork,
                    "Attach the signed agreement for {Name} to their record",
                    days: 7);
                rule.Then(AutomationActionKind.RaiseWork,
                    "Name the main contact and support channel for {Name}",
                    days: 5);
                rule.Then(AutomationActionKind.Notify,
                    "{Name} ({Code}) has been taken on as a client.",
                    who: "role:" + Roles.Sales);
                rule.Then(AutomationActionKind.Notify,
                    "{Name} ({Code}) is a new client. Check the billing details before invoicing.",
                    who: "role:" + Roles.FinanceManager);
            }),

        new(
            OverdueInvoice,
            "Chase an overdue invoice",
            nameof(InvoiceOverdue),
            "Section 31's own example. When an invoice is seven days or more overdue, tell finance "
            + "and sales and put the chase on the board.",
            rule =>
            {
                rule.When(nameof(InvoiceOverdue.DaysOverdue), ConditionOperator.AtLeast, "7");
                rule.Then(AutomationActionKind.Notify,
                    "Invoice {Number} is {DaysOverdue} days overdue.",
                    who: "role:" + Roles.FinanceManager);
                rule.Then(AutomationActionKind.Notify,
                    "Invoice {Number} is {DaysOverdue} days overdue. The client may need a call.",
                    who: "role:" + Roles.Sales);
                rule.Then(AutomationActionKind.RaiseWork,
                    "Chase payment of invoice {Number}, {DaysOverdue} days overdue",
                    days: 2);
            }),
    ];
}
