using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.People;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Work;

namespace JiranisokoTech.Application.Work;

/// <summary>
/// Everything anybody does to a project or a piece of work.
/// </summary>
/// <remarks>
/// The item holds its own state machine; this holds the rules that need the rest
/// of the system — whether the person being handed the work is in a position to
/// take it, whether a project has anything still open under it.
/// </remarks>
public sealed class WorkService(
    IWorkRepository work,
    IPeopleRepository people,
    IPlanningRepository planning,
    IClock clock)
{
    public async Task<Project> BeginProjectAsync(
        string name,
        string? code = null,
        Guid? departmentId = null,
        DateOnly? dueOn = null,
        CancellationToken cancellationToken = default)
    {
        var handle = Slug.From(code ?? name);

        if (await work.CodeTakenAsync(handle.Value, null, cancellationToken))
        {
            throw new InvalidOperationException(
                $"Another project already uses the code '{handle.Value}'. Codes end up in "
                + "addresses and commit messages, so they have to be unique.");
        }

        if (departmentId is { } department
            && !await people.DepartmentExistsAsync(department, cancellationToken))
        {
            throw new InvalidOperationException("That department does not exist.");
        }

        var project = Project.Begin(name, handle.Value, departmentId, dueOn);

        work.Add(project);
        await work.SaveAsync(cancellationToken);

        return project;
    }

    /// <summary>
    /// Mark a project delivered.
    /// </summary>
    /// <remarks>
    /// Refused while anything under it is still open. A project marked delivered
    /// with six live items is either wrong or a decision somebody should make
    /// deliberately — by cancelling them — rather than one the system makes
    /// silently on their behalf.
    /// </remarks>
    public async Task DeliverProjectAsync(
        Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await RequiredProject(projectId, cancellationToken);
        var open = await work.OpenItemCountAsync(projectId, cancellationToken);

        if (open > 0)
        {
            throw new InvalidOperationException(
                $"{project.Name} still has {open} item(s) open. Finish or cancel them first, so "
                + "that delivering it is a statement rather than a guess.");
        }

        project.Deliver(clock.Now);
        await work.SaveAsync(cancellationToken);
    }

    public async Task ActivateProjectAsync(
        Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await RequiredProject(projectId, cancellationToken);

        project.Activate(clock.Now);
        await work.SaveAsync(cancellationToken);
    }

    public async Task HoldProjectAsync(
        Guid projectId, string reason, CancellationToken cancellationToken = default)
    {
        var project = await RequiredProject(projectId, cancellationToken);

        project.Hold(reason, clock.Now);
        await work.SaveAsync(cancellationToken);
    }

    public async Task CancelProjectAsync(
        Guid projectId, string reason, CancellationToken cancellationToken = default)
    {
        var project = await RequiredProject(projectId, cancellationToken);

        project.Cancel(reason, clock.Now);
        await work.SaveAsync(cancellationToken);
    }

    public async Task LeadProjectAsync(
        Guid projectId, Guid? employeeId, CancellationToken cancellationToken = default)
    {
        var project = await RequiredProject(projectId, cancellationToken);

        if (employeeId is { } id)
        {
            await RequiredAvailable(id, cancellationToken);
        }

        project.LeadBy(employeeId);
        await work.SaveAsync(cancellationToken);
    }

    public async Task<WorkItem> RaiseAsync(
        string title,
        Guid raisedById,
        Guid? projectId = null,
        Guid? assigneeId = null,
        Priority priority = Priority.Normal,
        WorkItemKind kind = WorkItemKind.Task,
        CancellationToken cancellationToken = default)
    {
        if (projectId is { } id)
        {
            var project = await RequiredProject(id, cancellationToken);

            if (!project.IsRunning)
            {
                throw new InvalidOperationException(
                    $"{project.Name} is {project.Status.ToString().ToLowerInvariant()}. New work "
                    + "cannot be added to it.");
            }
        }

        /*
         * The next number, taken the same way an invoice number is: read the
         * last one and add one. Two people raising work in the same instant
         * would collide on the unique index rather than quietly share a
         * number, which is the right way round — a duplicate reference would
         * send a developer's commits to somebody else's task.
         */
        var number = await work.LastNumberAsync(cancellationToken) + 1;

        var item = WorkItem.Raise(number, title, raisedById, projectId, priority, kind);

        if (assigneeId is { } assignee)
        {
            await RequiredAvailable(assignee, cancellationToken);
            item.AssignTo(assignee);
        }

        work.Add(item);
        await work.SaveAsync(cancellationToken);

        return item;
    }

    /// <summary>
    /// Hand work to somebody.
    /// </summary>
    /// <remarks>
    /// Only to somebody who can act on it. Assigning work to a person who has
    /// left, or who has not started, produces a board that looks staffed and is
    /// not — and nobody notices until the deadline.
    /// </remarks>
    public async Task AssignAsync(
        Guid workItemId, Guid? employeeId, CancellationToken cancellationToken = default)
    {
        var item = await Required(workItemId, cancellationToken);

        if (employeeId is { } id)
        {
            await RequiredAvailable(id, cancellationToken);
        }

        item.AssignTo(employeeId);
        await work.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Move work along the board.
    /// </summary>
    /// <remarks>
    /// The state machine itself is in the aggregate, and so is the refusal to finish something
    /// whose acceptance criteria are not met. What is here is the one rule that needs other
    /// rows: <b>nothing is done while something it waits on is not</b>. That is what a dependency
    /// means, and a board that lets the blocked card finish first is a board recording an order
    /// of events that did not happen.
    /// </remarks>
    public async Task MoveAsync(
        Guid workItemId,
        WorkItemStatus status,
        string? because = null,
        CancellationToken cancellationToken = default)
    {
        var item = await Required(workItemId, cancellationToken);

        if (status is WorkItemStatus.Done or WorkItemStatus.Deployed)
        {
            var waiting = await Waiting(workItemId, cancellationToken);

            if (waiting.Count > 0)
            {
                var names = string.Join(", ", waiting.Select(one => one.Reference));

                throw new InvalidOperationException(
                    $"{item.Reference} waits on {names}, which "
                    + $"{(waiting.Count == 1 ? "is" : "are")} not finished. Finish "
                    + $"{(waiting.Count == 1 ? "it" : "them")} first, or drop the dependency if "
                    + "it is no longer real.");
            }
        }

        item.MoveTo(status, clock.Now, because);
        await work.SaveAsync(cancellationToken);
    }

    /// <summary>Put a word on a piece of work.</summary>
    public async Task LabelAsync(
        Guid workItemId, string text, CancellationToken cancellationToken = default)
    {
        var item = await Required(workItemId, cancellationToken);

        item.Label(text);
        await work.SaveAsync(cancellationToken);
    }

    public async Task UnlabelAsync(
        Guid workItemId, string text, CancellationToken cancellationToken = default)
    {
        var item = await Required(workItemId, cancellationToken);

        item.Unlabel(text);
        await work.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Say something about a piece of work.
    /// </summary>
    /// <remarks>
    /// The author is passed in rather than read here, because the only honest source for who is
    /// speaking is whoever is signed in — and a service that guessed would be one where a
    /// comment can be put in somebody else's name.
    /// </remarks>
    public async Task<WorkItemComment> CommentAsync(
        Guid workItemId,
        Guid byEmployeeId,
        string body,
        CancellationToken cancellationToken = default)
    {
        var item = await Required(workItemId, cancellationToken);
        var comment = item.Say(byEmployeeId, body, clock.Now);

        await work.SaveAsync(cancellationToken);

        return comment;
    }

    public async Task RewordAsync(
        Guid workItemId,
        Guid commentId,
        Guid byEmployeeId,
        string body,
        CancellationToken cancellationToken = default)
    {
        var item = await Required(workItemId, cancellationToken);

        item.Reword(commentId, byEmployeeId, body, clock.Now);
        await work.SaveAsync(cancellationToken);
    }

    /// <summary>Add a line to what done looks like.</summary>
    public async Task NeedsAsync(
        Guid workItemId, string text, CancellationToken cancellationToken = default)
    {
        var item = await Required(workItemId, cancellationToken);

        item.Needs(text);
        await work.SaveAsync(cancellationToken);
    }

    public async Task MetAsync(
        Guid workItemId,
        Guid lineId,
        Guid byEmployeeId,
        bool met,
        CancellationToken cancellationToken = default)
    {
        var item = await Required(workItemId, cancellationToken);

        if (met)
        {
            item.Met(lineId, byEmployeeId, clock.Now);
        }
        else
        {
            item.NotMet(lineId);
        }

        await work.SaveAsync(cancellationToken);
    }

    /// <summary>Strike out a line that turned out not to apply, saying why.</summary>
    public async Task DropLineAsync(
        Guid workItemId, Guid lineId, string because, CancellationToken cancellationToken = default)
    {
        var item = await Required(workItemId, cancellationToken);

        item.DropLine(lineId, because);
        await work.SaveAsync(cancellationToken);
    }

    public async Task RemoveLineAsync(
        Guid workItemId, Guid lineId, CancellationToken cancellationToken = default)
    {
        var item = await Required(workItemId, cancellationToken);

        item.RemoveLine(lineId);
        await work.SaveAsync(cancellationToken);
    }

    /// <summary>The unfinished work this one waits on.</summary>
    private async Task<List<WorkItem>> Waiting(
        Guid workItemId, CancellationToken cancellationToken)
    {
        var links = await planning.LinksForAsync(workItemId, cancellationToken);
        var blockerIds = links
            .Where(one => one.BlockedId == workItemId)
            .Select(one => one.BlockerId)
            .ToList();

        return blockerIds.Count == 0
            ? []
            : [.. (await planning.ByIdsAsync(blockerIds, cancellationToken))
                .Where(one => one.IsOpen)];
    }

    public async Task UpdateAsync(
        Guid workItemId,
        string title,
        string? detail,
        Priority priority,
        int? estimateMinutes,
        DateOnly? dueOn,
        CancellationToken cancellationToken = default)
    {
        var item = await Required(workItemId, cancellationToken);

        item.Retitle(title);
        item.Describe(detail);
        item.Prioritise(priority);
        item.Estimate(estimateMinutes);
        item.DueBy(dueOn);

        await work.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Take open work off somebody who is going, and leave it unassigned.
    /// </summary>
    /// <remarks>
    /// Called when somebody leaves. Unassigned rather than handed to their
    /// manager: work needing a new owner should appear as needing one, and
    /// silently loading it onto a manager who has not agreed to it is how it
    /// gets lost twice.
    /// </remarks>
    public async Task<int> ReleaseWorkOfAsync(
        Guid employeeId, CancellationToken cancellationToken = default)
    {
        var items = await work.OpenWorkForAsync(employeeId, cancellationToken);

        foreach (var item in items)
        {
            item.AssignTo(null);
        }

        if (items.Count > 0)
        {
            await work.SaveAsync(cancellationToken);
        }

        return items.Count;
    }

    private async Task<Employee> RequiredAvailable(Guid id, CancellationToken cancellationToken)
    {
        var employee = await people.FindAsync(id, cancellationToken)
            ?? throw new InvalidOperationException("That person is not on the staff list.");

        if (!employee.IsAssignable)
        {
            throw new InvalidOperationException(
                employee.Status == EmploymentStatus.Invited
                    ? $"{employee.FullName} has not started yet, so work cannot be given to them."
                    : $"{employee.FullName} is {employee.Status.ToString().ToLowerInvariant()}, "
                      + "so work cannot be given to them.");
        }

        return employee;
    }

    private async Task<WorkItem> Required(Guid id, CancellationToken cancellationToken) =>
        await work.FindAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("There is no work with that identifier.");

    /// <summary>
    /// Set or clear what a project was agreed to cost.
    /// </summary>
    /// <remarks>
    /// A budget and not a forecast: it is what was agreed at the start, and it does not
    /// move as the work does. The whole use of it is comparing what was agreed against
    /// what happened, and a budget that drifted to match the spending would always be met.
    /// </remarks>
    public async Task BudgetAsync(
        Guid projectId,
        long? minorUnits,
        string? currency,
        CancellationToken cancellationToken = default)
    {
        var project = await RequiredProject(projectId, cancellationToken);

        if (minorUnits is not null && string.IsNullOrWhiteSpace(currency))
        {
            throw new InvalidOperationException(
                "A budget needs a currency. A bare number is not an amount of money.");
        }

        project.Budgeted(minorUnits is { } amount
            ? Domain.Common.Money.Of(amount, currency!.Trim().ToUpperInvariant())
            : null);

        await work.SaveAsync(cancellationToken);
    }

    private async Task<Project> RequiredProject(Guid id, CancellationToken cancellationToken) =>
        await work.FindProjectAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("There is no project with that identifier.");
}
