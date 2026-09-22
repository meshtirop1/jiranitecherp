using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Approvals;

/// <summary>
/// A decision somebody is waiting on, and the people it has to pass through.
/// </summary>
/// <remarks>
/// The subject is stored as a type name and an identifier rather than a foreign
/// key, exactly as audit entries are. Requisitions, leave and expenses all need
/// approving and live in different tables; a column per kind would mean altering
/// this table for every new one, and a real foreign key would mean a request
/// could not outlive the thing it was about.
///
/// The failure this design exists to prevent is one the system it replaces
/// shipped: a chain in which every step had been passed over sat at Pending for
/// ever, with no button anywhere that could finish it. Nothing was wrong with
/// any single step. The fault was that "decided" and "cannot be decided" had
/// been made to look the same, and nothing checked whether anybody was left. The
/// rules below are written so that state cannot be reached.
/// </remarks>
public sealed class ApprovalRequest : Entity, IAuditable
{
    private readonly List<ApprovalStep> _steps = [];

    private ApprovalRequest()
    {
        SubjectType = string.Empty;
        Action = string.Empty;
    }

    private ApprovalRequest(
        string subjectType,
        Guid subjectId,
        string action,
        Guid requestedById,
        DateTimeOffset at,
        IEnumerable<ApprovalStep> steps)
    {
        SubjectType = Require(subjectType, nameof(subjectType));
        SubjectId = subjectId;
        Action = Require(action, nameof(action));
        RequestedById = requestedById;
        RequestedAt = at;
        Status = ApprovalStatus.Pending;

        _steps.AddRange(steps);

        if (_steps.Count == 0)
        {
            // A chain with no steps approves nothing and waits for nobody. It is
            // not an approval; whatever raised it should either act directly or
            // name somebody.
            throw new ArgumentException(
                "An approval needs at least one step. A chain nobody is on cannot be finished.",
                nameof(steps));
        }

        Raise(new ApprovalRequested(Id, subjectType, subjectId, action, requestedById, _steps.Count));
    }

    /// <summary>
    /// Open a chain over something, to be decided by these people in this order.
    /// </summary>
    public static ApprovalRequest Open(
        string subjectType,
        Guid subjectId,
        string action,
        Guid requestedById,
        DateTimeOffset at,
        params Guid[] deciders)
    {
        if (deciders.Length == 0)
        {
            throw new ArgumentException(
                "Name at least one person to decide it.", nameof(deciders));
        }

        if (deciders.Distinct().Count() != deciders.Length)
        {
            // The same person twice is not two decisions. It is one person being
            // asked the same question again, which nobody does, so the chain
            // would stall on the second ask.
            throw new ArgumentException(
                "The same person appears twice in the chain. Each step needs a different decider.",
                nameof(deciders));
        }

        var steps = deciders.Select((decider, index) => ApprovalStep.For(index + 1, decider));

        return new ApprovalRequest(subjectType, subjectId, action, requestedById, at, steps);
    }

    /// <summary>The type name of what is being decided, e.g. "JobRequisition".</summary>
    public string SubjectType { get; private init; }

    public Guid SubjectId { get; private init; }

    /// <summary>What is being asked, e.g. "requisition.open".</summary>
    public string Action { get; private init; }

    public Guid RequestedById { get; private init; }

    public DateTimeOffset RequestedAt { get; private init; }

    public ApprovalStatus Status { get; private set; }

    public DateTimeOffset? SettledAt { get; private set; }

    /// <summary>Why it was refused or withdrawn. Null while pending or approved.</summary>
    public string? Outcome { get; private set; }

    public IReadOnlyList<ApprovalStep> Steps => _steps.OrderBy(step => step.Order).ToList();

    public bool IsSettled => Status != ApprovalStatus.Pending;

    /// <summary>
    /// The step waiting on somebody now, or null when there is none.
    /// </summary>
    /// <remarks>
    /// Strictly in order. Letting a later approver go first reads as efficient
    /// and means the head of department approves something their lead has not
    /// yet seen, which is the opposite of what a chain is for.
    /// </remarks>
    public ApprovalStep? CurrentStep =>
        Status == ApprovalStatus.Pending
            ? _steps.OrderBy(step => step.Order).FirstOrDefault(step => step.IsWaiting)
            : null;

    /// <summary>Who the request is waiting on right now.</summary>
    public Guid? WaitingOn => CurrentStep?.DeciderId;

    /// <summary>
    /// Approve the step this is waiting on.
    /// </summary>
    public void Approve(Guid byEmployeeId, DateTimeOffset at, string? note = null)
    {
        var step = StepFor(byEmployeeId);

        step.Approve(byEmployeeId, at, note);

        Raise(new ApprovalStepDecided(Id, step.Order, byEmployeeId, StepStatus.Approved, note));

        // The chain finishes when nothing is left waiting. Skipped steps do not
        // hold it open — being unable to ask somebody is not the same as their
        // having said no.
        if (_steps.All(other => !other.IsWaiting))
        {
            Settle(ApprovalStatus.Approved, at, null);
        }
    }

    /// <summary>
    /// Refuse it, which ends the chain.
    /// </summary>
    /// <remarks>
    /// One no is enough. Carrying on up the chain after a refusal means asking
    /// somebody to overrule a colleague without telling them that is what they
    /// are doing, and produces an approval whose history reads as agreement.
    /// </remarks>
    public void Refuse(Guid byEmployeeId, DateTimeOffset at, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException(
                "Say why it was refused. The person who asked has to know what to change.",
                nameof(reason));
        }

        var step = StepFor(byEmployeeId);

        step.Refuse(byEmployeeId, at, reason);

        Raise(new ApprovalStepDecided(Id, step.Order, byEmployeeId, StepStatus.Refused, reason));

        Settle(ApprovalStatus.Refused, at, reason.Trim());
    }

    /// <summary>
    /// Pass over a step nobody can decide.
    /// </summary>
    /// <remarks>
    /// For a vacant post or a decider who has left. The rule that matters is the
    /// last one: the final waiting step cannot be skipped. Allowing it produces
    /// a chain where every step has been passed over and the request sits at
    /// Pending for ever with nothing that can finish it — which is precisely the
    /// state the system this replaces got into, and could not get out of.
    ///
    /// Whoever would have skipped that last step has to decide it instead, or
    /// hand it to somebody who can.
    /// </remarks>
    public void Skip(int order, DateTimeOffset at, string reason)
    {
        if (IsSettled)
        {
            throw new InvalidOperationException("This has already been settled.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Say why the step is being passed over.", nameof(reason));
        }

        var step = _steps.SingleOrDefault(candidate => candidate.Order == order)
            ?? throw new InvalidOperationException($"There is no step {order} on this approval.");

        if (!step.IsWaiting)
        {
            throw new InvalidOperationException($"Step {order} has already been settled.");
        }

        var waiting = _steps.Count(candidate => candidate.IsWaiting);

        if (waiting == 1)
        {
            throw new InvalidOperationException(
                "This is the only step left. Passing it over would leave the request with "
                + "nobody to decide it and no way to finish, so it has to be decided or "
                + "reassigned instead.");
        }

        step.Skip(at, reason);

        Raise(new ApprovalStepDecided(Id, step.Order, null, StepStatus.Skipped, reason));
    }

    /// <summary>
    /// Hand a waiting step to somebody else.
    /// </summary>
    /// <remarks>
    /// The way out of a chain that would otherwise be stuck: the named decider
    /// has left, and the last step cannot be skipped, so it is given to the
    /// person who has taken over from them.
    /// </remarks>
    public void Reassign(int order, Guid toEmployeeId, DateTimeOffset at)
    {
        if (IsSettled)
        {
            throw new InvalidOperationException("This has already been settled.");
        }

        var step = _steps.SingleOrDefault(candidate => candidate.Order == order)
            ?? throw new InvalidOperationException($"There is no step {order} on this approval.");

        if (!step.IsWaiting)
        {
            throw new InvalidOperationException($"Step {order} has already been settled.");
        }

        if (_steps.Any(other => other.Order != order && other.DeciderId == toEmployeeId))
        {
            throw new InvalidOperationException(
                "That person is already on this chain, and nobody is asked the same question "
                + "twice.");
        }

        var from = step.DeciderId;

        step.HandTo(toEmployeeId);

        Raise(new ApprovalStepReassigned(Id, order, from, toEmployeeId, at));
    }

    /// <summary>
    /// Taken back by whoever asked.
    /// </summary>
    /// <remarks>
    /// Only before anybody has decided. Withdrawing after an approval would
    /// erase a decision somebody made and is answerable for.
    /// </remarks>
    public void Withdraw(Guid byEmployeeId, DateTimeOffset at, string reason)
    {
        if (IsSettled)
        {
            throw new InvalidOperationException("This has already been settled.");
        }

        if (byEmployeeId != RequestedById)
        {
            throw new InvalidOperationException(
                "Only the person who asked can withdraw it. Anybody else refuses it, which "
                + "leaves a decision on the record.");
        }

        if (_steps.Any(step => step.Status is StepStatus.Approved or StepStatus.Refused))
        {
            throw new InvalidOperationException(
                "Somebody has already decided a step, so this can no longer be withdrawn. "
                + "Refusing it keeps their decision on the record.");
        }

        Settle(ApprovalStatus.Withdrawn, at, Require(reason, nameof(reason)));
    }

    /// <summary>Nothing here is a secret.</summary>
    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private ApprovalStep StepFor(Guid employeeId)
    {
        if (IsSettled)
        {
            throw new InvalidOperationException(
                $"This was already {Status.ToString().ToLowerInvariant()}.");
        }

        var step = CurrentStep
            ?? throw new InvalidOperationException("There is no step waiting on a decision.");

        if (step.DeciderId != employeeId)
        {
            /*
             * Named, rather than "not permitted". Somebody looking at a request
             * that is not theirs to decide needs to know who it is with, and a
             * refusal that will not say wastes an afternoon of asking around.
             */
            throw new InvalidOperationException(
                "This is not yours to decide. It is with somebody else at step "
                + $"{step.Order} of {_steps.Count}.");
        }

        return step;
    }

    private void Settle(ApprovalStatus status, DateTimeOffset at, string? outcome)
    {
        Status = status;
        SettledAt = at;
        Outcome = outcome;

        Raise(new ApprovalSettled(Id, SubjectType, SubjectId, Action, status, outcome, at));
    }

    private static string Require(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();
}
