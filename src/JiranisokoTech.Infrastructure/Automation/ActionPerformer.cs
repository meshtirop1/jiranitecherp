using System.Text.Json;
using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Automation;
using JiranisokoTech.Application.Integrations;
using JiranisokoTech.Application.Mail;
using JiranisokoTech.Application.Notices;
using JiranisokoTech.Application.People;
using JiranisokoTech.Application.Work;
using JiranisokoTech.Domain.Automation;
using JiranisokoTech.Domain.Integrations;
using JiranisokoTech.Domain.Notices;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.Mail;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Automation;

/// <summary>
/// THEN: carry out one action of one rule, through the service a person would use.
/// </summary>
/// <remarks>
/// Every action goes through an application service that already exists — the work service
/// raises the work, the notice service writes the notice, the onboarding service keeps the
/// checklist — so a rule is held to exactly the rules a person on the matching page is held to.
/// A project that has closed refuses new work from a rule the way it refuses it from the board.
///
/// Resolved in a scope of its own for each action, by <see cref="CarryOutAutomation"/>, so an
/// action that fails halfway leaves nothing half-tracked for the next one to save by accident.
///
/// Refusals are thrown as <see cref="InvalidOperationException"/>, the codebase's way of saying
/// "no, and here is why in words"; the caller records them against the step and moves on,
/// because the same request would be refused the same way on every retry. Anything else is a
/// failure the outbox should try again.
/// </remarks>
public sealed class ActionPerformer(
    AppDbContext database,
    WorkService work,
    NoticeService notices,
    OnboardingService onboarding,
    IIntegrationRepository integrations,
    MailRecipients recipients,
    IMailer mailer,
    IWhereThisLives where,
    IClock clock)
{
    private static readonly JsonSerializerOptions Format = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Do it. Returns what was done, in words, for the run history.</summary>
    /// <param name="made">Called the moment a work item exists, before anything else is done to it.</param>
    public Task<string> PerformAsync(
        AutomationRule rule,
        AutomationAction action,
        Facts facts,
        Guid? alreadyMade,
        Func<Guid, Task> made,
        CancellationToken cancellationToken = default) => action.Kind switch
    {
        AutomationActionKind.RaiseWork =>
            RaiseWorkAsync(rule, action, facts, alreadyMade, made, cancellationToken),
        AutomationActionKind.Notify => NotifyAsync(rule, action, facts, cancellationToken),
        AutomationActionKind.Email => EmailAsync(rule, action, facts, cancellationToken),
        AutomationActionKind.OnboardingSteps => ChecklistAsync(rule, action, facts, cancellationToken),
        AutomationActionKind.Webhook => WebhookAsync(rule, action, facts, cancellationToken),
        _ => throw new InvalidOperationException($"A rule cannot {action.Kind}."),
    };

    private async Task<string> RaiseWorkAsync(
        AutomationRule rule,
        AutomationAction action,
        Facts facts,
        Guid? alreadyMade,
        Func<Guid, Task> made,
        CancellationToken cancellationToken)
    {
        var title = Wording.Fill(action.Text ?? rule.Name, facts, rule.Name);

        if (title.Length > 300)
        {
            title = title[..300];
        }

        var itemId = alreadyMade;
        var said = string.Empty;

        if (itemId is null)
        {
            var projectId = Who.Parse(action.Where) switch
            {
                (WhoKind.Field, var field) => facts.Reference(field),
                (WhoKind.Project, var id) => Guid.TryParse(id, out var chosen) ? chosen : null,
                _ => null,
            };

            Guid? assignee = null;

            if (action.Who is not null)
            {
                var people = await PeopleAsync(action.Who, facts, cancellationToken);

                assignee = people.FirstOrDefault();

                if (assignee is null)
                {
                    // Raised for nobody rather than not raised. Work that needs an owner
                    // showing as unowned is visible; work that never appeared is not.
                    said = $" It was meant for {Who.Describe(facts.Trigger, action.Who)}, "
                        + "and there was nobody, so it is unassigned.";
                }
            }

            // Raised by the firm, as the public API raises work for a key: a rule is not a
            // person, and naming whoever wrote the rule would put words in their mouth. The
            // audit trail names the rule.
            var item = await work.RaiseAsync(
                title, Guid.Empty, projectId, assignee, Priority.Normal, WorkItemKind.Task,
                cancellationToken);

            itemId = item.Id;
            await made(item.Id);
        }

        var detail = Wording.Fill(action.Body ?? string.Empty, facts, rule.Name).Trim();
        var provenance = $"Raised by the automation rule “{rule.Name}” when "
            + $"{facts.Trigger.Label.ToLowerInvariant()}: {Wording.Fill(facts.Trigger.Summary, facts, rule.Name)}.";

        await work.UpdateAsync(
            itemId.Value,
            title,
            detail.Length == 0 ? provenance : $"{detail}\n\n{provenance}",
            Priority.Normal,
            null,
            action.Days is { } days ? clock.Today.AddDays(days) : null,
            cancellationToken);

        var raised = await database.WorkItems
            .AsNoTracking()
            .FirstAsync(one => one.Id == itemId, cancellationToken);

        return $"Raised {raised.Reference}, \u201c{title}\u201d.{said}";
    }

    private async Task<string> NotifyAsync(
        AutomationRule rule, AutomationAction action, Facts facts, CancellationToken cancellationToken)
    {
        var people = await Required(action, facts, cancellationToken);
        var subject = Clip(Wording.Fill(action.Text ?? rule.Name, facts, rule.Name), 300);
        var link = Wording.Link(facts);

        foreach (var person in people)
        {
            await notices.TellAsync(person, NoticeKind.Automation, subject, link, cancellationToken);
        }

        return $"Told {people.Count} {(people.Count == 1 ? "person" : "people")}.";
    }

    private async Task<string> EmailAsync(
        AutomationRule rule, AutomationAction action, Facts facts, CancellationToken cancellationToken)
    {
        var people = await Required(action, facts, cancellationToken);
        var subject = Clip(Wording.Fill(action.Text ?? rule.Name, facts, rule.Name), 300);
        var body = Wording.Fill(action.Body ?? string.Empty, facts, rule.Name);
        var link = where.Reachable(Wording.Link(facts));
        var sent = 0;

        foreach (var person in people)
        {
            if (await recipients.ForAsync(person, cancellationToken) is not { } to)
            {
                continue;
            }

            await mailer.SendAsync(
                Letters.FromARule(to.Address, to.Name, subject, body, link, rule.Name),
                cancellationToken);

            sent++;
        }

        if (sent == 0)
        {
            throw new InvalidOperationException(
                $"None of the {people.Count} people it was for has a mailbox here — no sign-in, "
                + "or one that has been switched off.");
        }

        return sent == people.Count
            ? $"Emailed {sent} {(sent == 1 ? "person" : "people")}."
            : $"Emailed {sent} of {people.Count}; the rest have no mailbox here.";
    }

    /// <summary>
    /// Add lines to the joiner's checklist, starting one when there is none.
    /// </summary>
    /// <remarks>
    /// Lines already on the list are not added twice, which is what makes this safe to run
    /// again — and makes a second rule adding "Laptop prepared" harmless rather than a duplicate
    /// somebody has to tick off.
    /// </remarks>
    private async Task<string> ChecklistAsync(
        AutomationRule rule, AutomationAction action, Facts facts, CancellationToken cancellationToken)
    {
        var person = (await Required(action, facts, cancellationToken))[0];

        var employee = await database.Employees
            .AsNoTracking()
            .FirstOrDefaultAsync(one => one.Id == person, cancellationToken)
            ?? throw new InvalidOperationException("That person is no longer on the staff list.");

        if (employee.Status == EmploymentStatus.Left)
        {
            throw new InvalidOperationException(
                $"{employee.FullName} has left, so there is nobody to get ready.");
        }

        var list = await onboarding.ForAsync(person, cancellationToken)
            ?? await onboarding.BeginAsync(person, employee.StartsOn, cancellationToken);

        var have = list.Steps.Select(one => one.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;

        foreach (var line in Wording.Lines(Wording.Fill(action.Body ?? string.Empty, facts, rule.Name)))
        {
            var name = Clip(line, 200);

            if (have.Add(name))
            {
                await onboarding.AlsoAsync(person, name, cancellationToken);
                added++;
            }
        }

        return added == 0
            ? $"Every line was already on {employee.FullName}'s checklist."
            : $"Added {added} line(s) to {employee.FullName}'s checklist.";
    }

    /// <summary>
    /// Queue a signed notification to one of the outgoing webhook subscriptions.
    /// </summary>
    /// <remarks>
    /// Queued, not sent: the outbound sender delivers it with its own retries, signature and
    /// dead-letter, and a rule waiting on somebody else's server would hold up every run
    /// behind it. Only to a subscription that exists and is switched on — a rule cannot name
    /// an address, because an address nobody set up on the webhooks page has no secret to sign
    /// with and nobody who agreed to receive it.
    /// </remarks>
    private async Task<string> WebhookAsync(
        AutomationRule rule, AutomationAction action, Facts facts, CancellationToken cancellationToken)
    {
        if (action.SubscriptionId is not { } id
            || await integrations.FindAsync(id, cancellationToken) is not { } subscription)
        {
            throw new InvalidOperationException("The webhook subscription it names no longer exists.");
        }

        if (!subscription.IsActive)
        {
            throw new InvalidOperationException(
                $"The subscription “{subscription.Name}” is switched off.");
        }

        var name = $"automation.{facts.Trigger.Name}";
        var data = facts.Trigger.Fields.ToDictionary(field => field.Name, field => facts[field.Name]);
        var body = JsonSerializer.Serialize(
            new { @event = name, at = clock.Now, rule = rule.Name, data }, Format);

        integrations.Add(OutboundDelivery.Queue(subscription.Id, name, body, clock.Now));
        await integrations.SaveAsync(cancellationToken);

        return $"Queued a notification to “{subscription.Name}”.";
    }

    private async Task<List<Guid>> Required(
        AutomationAction action, Facts facts, CancellationToken cancellationToken)
    {
        var people = await PeopleAsync(action.Who, facts, cancellationToken);

        return people.Count > 0
            ? people
            : throw new InvalidOperationException(
                $"It was for {Who.Describe(facts.Trigger, action.Who)}, and there was nobody.");
    }

    /// <summary>
    /// Who a "who" names, for this event, as staff record identifiers.
    /// </summary>
    /// <remarks>
    /// Only people still here: somebody who has left is not told things or given work by a
    /// rule, for the same reason their open work is released when they go. At most
    /// <see cref="Who.MostPeople"/>, however large the role.
    /// </remarks>
    private async Task<List<Guid>> PeopleAsync(
        string? spec, Facts facts, CancellationToken cancellationToken)
    {
        var (kind, argument) = Who.Parse(spec);
        List<Guid> named;

        switch (kind)
        {
            case WhoKind.Field:
                named = facts.Reference(argument) is { } one ? [one] : [];
                break;

            case WhoKind.Manager:
                named = facts.Reference(argument) is { } person
                    ? await ManagerOfAsync(person, cancellationToken)
                    : [];
                break;

            case WhoKind.Lead:
                named = facts.Reference(argument) is { } project
                    ? await database.Projects.AsNoTracking()
                        .Where(one => one.Id == project && one.LeadId != null)
                        .Select(one => one.LeadId!.Value)
                        .ToListAsync(cancellationToken)
                    : [];
                break;

            case WhoKind.Head:
                named = facts.Reference(argument) is { } department
                    ? await database.Departments.AsNoTracking()
                        .Where(one => one.Id == department && one.HeadEmployeeId != null)
                        .Select(one => one.HeadEmployeeId!.Value)
                        .ToListAsync(cancellationToken)
                    : [];
                break;

            case WhoKind.Role:
                var accounts = database.UserRoles
                    .Join(database.Roles, held => held.RoleId, role => role.Id,
                        (held, role) => new { held.UserId, role.Name })
                    .Where(held => held.Name == argument)
                    .Join(database.Users.Where(user => user.IsActive), held => held.UserId,
                        user => user.Id, (held, user) => user.Id);

                named = await database.Employees.AsNoTracking()
                    .Where(one => one.AccountId != null && accounts.Contains(one.AccountId.Value))
                    .OrderBy(one => one.FullName)
                    .Select(one => one.Id)
                    .Take(Who.MostPeople)
                    .ToListAsync(cancellationToken);
                break;

            case WhoKind.Person:
                named = Guid.TryParse(argument, out var chosen) ? [chosen] : [];
                break;

            default:
                named = [];
                break;
        }

        if (named.Count == 0)
        {
            return [];
        }

        return await database.Employees.AsNoTracking()
            .Where(one => named.Contains(one.Id) && one.Status != EmploymentStatus.Left)
            .Select(one => one.Id)
            .Take(Who.MostPeople)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Their manager, or the head of their department when nobody is named yet.
    /// </summary>
    /// <remarks>
    /// The fallback is for the moment the brief's new-joiner automation fires: when somebody is
    /// hired their reporting line is usually not set yet, and "notify the manager" would reach
    /// nobody on exactly the occasion it exists for. The head of the department they are joining
    /// is who that manager answers to, and the right person to hear first.
    /// </remarks>
    private async Task<List<Guid>> ManagerOfAsync(Guid person, CancellationToken cancellationToken)
    {
        var employee = await database.Employees.AsNoTracking()
            .Where(one => one.Id == person)
            .Select(one => new { one.ReportsToId, one.DepartmentId })
            .FirstOrDefaultAsync(cancellationToken);

        if (employee?.ReportsToId is { } manager)
        {
            return [manager];
        }

        if (employee?.DepartmentId is not { } department)
        {
            return [];
        }

        return await database.Departments.AsNoTracking()
            .Where(one => one.Id == department && one.HeadEmployeeId != null
                && one.HeadEmployeeId != person)
            .Select(one => one.HeadEmployeeId!.Value)
            .ToListAsync(cancellationToken);
    }

    private static string Clip(string text, int longest) =>
        text.Length > longest ? text[..longest] : text;
}
