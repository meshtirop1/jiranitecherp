using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.People;
using JiranisokoTech.Domain.Approvals;
using JiranisokoTech.Domain.People;

namespace JiranisokoTech.Application.Approvals;

/// <summary>
/// Opening chains and deciding them.
/// </summary>
/// <remarks>
/// The chain holds the rules about order and about the state it must never
/// reach. This holds the ones that need the roster: whether the people being
/// asked are in a position to answer, and who they should be when nobody has
/// said.
/// </remarks>
public sealed class ApprovalService(
    IApprovalRepository approvals,
    IPeopleRepository people,
    IClock clock)
{
    /// <summary>
    /// Open a chain over something, naming who decides it.
    /// </summary>
    public async Task<ApprovalRequest> RequestAsync(
        string subjectType,
        Guid subjectId,
        string action,
        Guid requestedById,
        IReadOnlyList<Guid> deciders,
        CancellationToken cancellationToken = default)
    {
        if (await approvals.IsPendingAsync(subjectType, subjectId, action, cancellationToken))
        {
            throw new InvalidOperationException(
                "There is already an approval open on this. Two live chains over one decision "
                + "produce two answers, and the second quietly overwrites the first.");
        }

        foreach (var decider in deciders)
        {
            await RequiredAvailable(decider, cancellationToken);
        }

        var request = ApprovalRequest.Open(
            subjectType, subjectId, action, requestedById, clock.Now, [.. deciders]);

        approvals.Add(request);
        await approvals.SaveAsync(cancellationToken);

        return request;
    }

    /// <summary>
    /// Open a chain that follows the reporting line above whoever asked.
    /// </summary>
    /// <remarks>
    /// The default when nobody has configured anything, and the right one for
    /// most decisions: the people who should answer for a request are the people
    /// the asker already answers to.
    ///
    /// It stops at the first manager who cannot decide rather than skipping past
    /// them, because a chain that silently reaches higher than intended is worse
    /// than one that refuses to open and says why.
    /// </remarks>
    public async Task<ApprovalRequest> RequestUpTheLineAsync(
        string subjectType,
        Guid subjectId,
        string action,
        Guid requestedById,
        int levels = 1,
        CancellationToken cancellationToken = default)
    {
        var lines = await people.ReportingLinesAsync(cancellationToken);

        var chain = ReportingLine
            .ChainAbove(requestedById, person => lines.GetValueOrDefault(person))
            .Take(levels)
            .ToList();

        if (chain.Count == 0)
        {
            var asker = await people.FindAsync(requestedById, cancellationToken);

            throw new InvalidOperationException(
                $"{asker?.FullName ?? "That person"} answers to nobody, so there is no one above "
                + "them to decide this. Name a decider, or give them a manager first.");
        }

        return await RequestAsync(
            subjectType, subjectId, action, requestedById, chain, cancellationToken);
    }

    public async Task ApproveAsync(
        Guid approvalId,
        Guid byEmployeeId,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        var request = await Required(approvalId, cancellationToken);

        request.Approve(byEmployeeId, clock.Now, note);
        await approvals.SaveAsync(cancellationToken);
    }

    public async Task RefuseAsync(
        Guid approvalId,
        Guid byEmployeeId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var request = await Required(approvalId, cancellationToken);

        request.Refuse(byEmployeeId, clock.Now, reason);
        await approvals.SaveAsync(cancellationToken);
    }

    public async Task SkipAsync(
        Guid approvalId,
        int order,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var request = await Required(approvalId, cancellationToken);

        request.Skip(order, clock.Now, reason);
        await approvals.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Hand a waiting step to somebody else.
    /// </summary>
    /// <remarks>
    /// The recovery path. When the named decider has gone and the step is the
    /// last one — so it cannot be passed over — this is what finishes the chain
    /// instead of leaving it stuck.
    /// </remarks>
    public async Task ReassignAsync(
        Guid approvalId,
        int order,
        Guid toEmployeeId,
        CancellationToken cancellationToken = default)
    {
        var request = await Required(approvalId, cancellationToken);

        await RequiredAvailable(toEmployeeId, cancellationToken);

        request.Reassign(order, toEmployeeId, clock.Now);
        await approvals.SaveAsync(cancellationToken);
    }

    public async Task WithdrawAsync(
        Guid approvalId,
        Guid byEmployeeId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var request = await Required(approvalId, cancellationToken);

        request.Withdraw(byEmployeeId, clock.Now, reason);
        await approvals.SaveAsync(cancellationToken);
    }

    private async Task<Employee> RequiredAvailable(Guid id, CancellationToken cancellationToken)
    {
        var employee = await people.FindAsync(id, cancellationToken)
            ?? throw new InvalidOperationException("That person is not on the staff list.");

        if (!employee.IsAssignable)
        {
            /*
             * The rule that keeps chains finishable. Somebody who has left or is
             * suspended will never answer, and putting them on a chain is how it
             * comes to sit at Pending until a person notices — which, for a step
             * three deep, is usually when somebody asks why a hire never
             * happened.
             */
            throw new InvalidOperationException(
                $"{employee.FullName} is {employee.Status.ToString().ToLowerInvariant()} and "
                + "cannot be asked to decide anything.");
        }

        return employee;
    }

    private async Task<ApprovalRequest> Required(Guid id, CancellationToken cancellationToken) =>
        await approvals.FindAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("There is no approval with that identifier.");
}
