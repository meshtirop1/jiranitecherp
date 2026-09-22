using JiranisokoTech.Domain.Approvals;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Approvals;

/// <summary>The reads the approval screens do.</summary>
public sealed class ApprovalQueries(AppDbContext database)
{
    /// <summary>
    /// What is waiting on this person right now.
    /// </summary>
    /// <remarks>
    /// The query everybody in the firm runs on opening the application, so it is
    /// written to be answerable from an index: pending chains only, and the step
    /// that is both theirs and still waiting.
    ///
    /// It also checks that no earlier step is still waiting. Without that, a
    /// person three deep in a chain would see a decision sitting in their list
    /// weeks before it was their turn, and learn to ignore the list.
    /// </remarks>
    public async Task<List<ApprovalRow>> WaitingOnAsync(
        Guid employeeId, CancellationToken cancellationToken = default)
    {
        var pending = await database.Approvals
            .AsNoTracking()
            .Include(request => request.Steps)
            .Where(request => request.Status == ApprovalStatus.Pending)
            .Where(request => request.Steps.Any(step =>
                step.DeciderId == employeeId && step.Status == StepStatus.Waiting))
            .OrderBy(request => request.RequestedAt)
            .ToListAsync(cancellationToken);

        var names = await database.Employees
            .AsNoTracking()
            .ToDictionaryAsync(person => person.Id, person => person.FullName, cancellationToken);

        return pending
            .Where(request => request.WaitingOn == employeeId)
            .Select(request => Row(request, names))
            .ToList();
    }

    public async Task<List<ApprovalRow>> PendingAsync(CancellationToken cancellationToken = default)
    {
        var pending = await database.Approvals
            .AsNoTracking()
            .Include(request => request.Steps)
            .Where(request => request.Status == ApprovalStatus.Pending)
            .OrderBy(request => request.RequestedAt)
            .ToListAsync(cancellationToken);

        var names = await database.Employees
            .AsNoTracking()
            .ToDictionaryAsync(person => person.Id, person => person.FullName, cancellationToken);

        return pending.Select(request => Row(request, names)).ToList();
    }

    public async Task<ApprovalRow?> FindAsync(
        Guid id, CancellationToken cancellationToken = default)
    {
        var request = await database.Approvals
            .AsNoTracking()
            .Include(one => one.Steps)
            .FirstOrDefaultAsync(one => one.Id == id, cancellationToken);

        if (request is null)
        {
            return null;
        }

        var names = await database.Employees
            .AsNoTracking()
            .ToDictionaryAsync(person => person.Id, person => person.FullName, cancellationToken);

        return Row(request, names);
    }

    private static ApprovalRow Row(ApprovalRequest request, Dictionary<Guid, string> names) =>
        new(
            request.Id,
            request.SubjectType,
            request.SubjectId,
            request.Action,
            request.Status,
            request.RequestedAt,
            request.RequestedById,
            names.GetValueOrDefault(request.RequestedById) ?? "Somebody who has gone",
            request.WaitingOn,
            request.WaitingOn is { } waiting ? names.GetValueOrDefault(waiting) : null,
            request.Outcome,
            request.Steps.Select(step => new StepRow(
                step.Order,
                step.DeciderId,
                names.GetValueOrDefault(step.DeciderId) ?? "Somebody who has gone",
                step.Status,
                step.DecidedAt,
                step.Note)).ToList());
}

public sealed record ApprovalRow(
    Guid Id,
    string SubjectType,
    Guid SubjectId,
    string Action,
    ApprovalStatus Status,
    DateTimeOffset RequestedAt,
    Guid RequestedById,
    string RequestedByName,
    Guid? WaitingOnId,
    string? WaitingOnName,
    string? Outcome,
    IReadOnlyList<StepRow> Steps);

public sealed record StepRow(
    int Order,
    Guid DeciderId,
    string DeciderName,
    StepStatus Status,
    DateTimeOffset? DecidedAt,
    string? Note);
