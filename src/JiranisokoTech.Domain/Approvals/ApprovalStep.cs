namespace JiranisokoTech.Domain.Approvals;

/// <summary>
/// One person being asked one question, at a fixed place in the chain.
/// </summary>
/// <remarks>
/// Not an <c>Entity</c>. A step has no life of its own — it is never looked up,
/// never referred to from anywhere else, and never exists apart from the request
/// it belongs to. Modelling it as an aggregate of its own would invite code that
/// decides a step without loading the chain, which is how a chain comes to be
/// half-decided in ways it forbids.
/// </remarks>
public sealed class ApprovalStep
{
    private ApprovalStep()
    {
    }

    private ApprovalStep(int order, Guid deciderId)
    {
        Id = Guid.CreateVersion7();
        Order = order;
        DeciderId = deciderId;
        Status = StepStatus.Waiting;
    }

    public static ApprovalStep For(int order, Guid deciderId) => new(order, deciderId);

    public Guid Id { get; private init; }

    /// <summary>Its place in the chain, from one.</summary>
    public int Order { get; private init; }

    /// <summary>Who is being asked. Changes only by reassignment.</summary>
    public Guid DeciderId { get; private set; }

    public StepStatus Status { get; private set; }

    /// <summary>
    /// Who actually decided it.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="DeciderId"/> on purpose. A step reassigned
    /// after a decision would otherwise rewrite who made it, and "who approved
    /// this?" is the question the whole table exists to answer.
    /// </remarks>
    public Guid? DecidedById { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    /// <summary>What they said. Required on a refusal, optional on an approval.</summary>
    public string? Note { get; private set; }

    public bool IsWaiting => Status == StepStatus.Waiting;

    internal void Approve(Guid byEmployeeId, DateTimeOffset at, string? note)
    {
        Settle(StepStatus.Approved, byEmployeeId, at, note);
    }

    internal void Refuse(Guid byEmployeeId, DateTimeOffset at, string reason)
    {
        Settle(StepStatus.Refused, byEmployeeId, at, reason);
    }

    internal void Skip(DateTimeOffset at, string reason)
    {
        // No decider: nobody decided it, which is the whole point of the state.
        // Recording the requester here would put a decision in somebody's name
        // that they never made.
        Settle(StepStatus.Skipped, null, at, reason);
    }

    internal void HandTo(Guid employeeId)
    {
        if (!IsWaiting)
        {
            throw new InvalidOperationException("A step that has been decided cannot be handed on.");
        }

        DeciderId = employeeId;
    }

    private void Settle(StepStatus status, Guid? byEmployeeId, DateTimeOffset at, string? note)
    {
        if (!IsWaiting)
        {
            throw new InvalidOperationException(
                $"Step {Order} was already {Status.ToString().ToLowerInvariant()}.");
        }

        Status = status;
        DecidedById = byEmployeeId;
        DecidedAt = at;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }
}
