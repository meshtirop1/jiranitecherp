using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Recruitment;

/// <summary>Where a request to hire has got to.</summary>
public enum RequisitionStatus
{
    /// <summary>Being written. Nobody has been asked for anything.</summary>
    Draft = 1,

    /// <summary>With the approvers.</summary>
    AwaitingApproval = 2,

    /// <summary>Agreed. Can be advertised and hired against.</summary>
    Approved = 3,

    /// <summary>Somebody said no, and said why.</summary>
    Refused = 4,

    /// <summary>Every post filled.</summary>
    Filled = 5,

    /// <summary>Given up on, or no longer wanted.</summary>
    Closed = 6,
}

/// <summary>
/// A request to hire somebody, and the count of how many.
/// </summary>
/// <remarks>
/// The first thing in this system that has to be approved before it does
/// anything, which is what the approval engine was built for. The requisition
/// knows nothing about chains: it is submitted, and later told that a decision
/// was made. Everything between those two points happens elsewhere.
/// </remarks>
public sealed class JobRequisition : Entity, IAuditable
{
    private JobRequisition()
    {
        JobTitle = string.Empty;
        Justification = string.Empty;
    }

    private JobRequisition(
        string jobTitle,
        Guid? departmentId,
        int headcount,
        string justification,
        Guid raisedById)
    {
        JobTitle = Require(jobTitle, nameof(jobTitle));
        DepartmentId = departmentId;
        Headcount = Positive(headcount);
        Justification = Require(justification, nameof(justification));
        RaisedById = raisedById;
        Status = RequisitionStatus.Draft;

        Raise(new RequisitionRaised(Id, JobTitle, departmentId, headcount, raisedById));
    }

    public static JobRequisition Raise(
        string jobTitle,
        Guid? departmentId,
        int headcount,
        string justification,
        Guid raisedById) =>
        new(jobTitle, departmentId, headcount, justification, raisedById);

    public string JobTitle { get; private set; }

    public Guid? DepartmentId { get; private set; }

    /// <summary>How many people are wanted.</summary>
    public int Headcount { get; private set; }

    /// <summary>
    /// How many have been hired against it so far.
    /// </summary>
    /// <remarks>
    /// Counted here rather than worked out from the applications, because it is
    /// the number every rule below depends on and a count that has to be
    /// recomputed is one that can disagree with itself halfway through a page.
    /// </remarks>
    public int HiredCount { get; private set; }

    /// <summary>Why the firm needs this, in the words the approvers will read.</summary>
    public string Justification { get; private set; }

    public Guid RaisedById { get; private init; }

    public RequisitionStatus Status { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    /// <summary>Why it was refused or closed.</summary>
    public string? Outcome { get; private set; }

    /// <summary>Posts still to fill. Never negative.</summary>
    public int Remaining => Math.Max(0, Headcount - HiredCount);

    /// <summary>Approved, with room left. The only state that can be hired against.</summary>
    public bool CanHire => Status == RequisitionStatus.Approved && Remaining > 0;

    /// <summary>Send it for approval.</summary>
    public void Submit(DateTimeOffset at)
    {
        if (Status != RequisitionStatus.Draft)
        {
            throw new InvalidOperationException(
                $"This has already been submitted and is {Say(Status)}.");
        }

        Status = RequisitionStatus.AwaitingApproval;

        Raise(new RequisitionSubmitted(Id, JobTitle, RaisedById, at));
    }

    /// <summary>
    /// Told that the approvers agreed.
    /// </summary>
    /// <remarks>
    /// Called by whatever is listening to the approval chain, never by a page.
    /// The requisition does not know how it was approved, or by whom, which is
    /// what keeps the two modules apart.
    /// </remarks>
    public void Approved(DateTimeOffset at)
    {
        if (Status != RequisitionStatus.AwaitingApproval)
        {
            throw new InvalidOperationException(
                $"This is {Say(Status)}, so there is no approval outstanding on it.");
        }

        Status = RequisitionStatus.Approved;
        DecidedAt = at;
        Outcome = null;

        Raise(new RequisitionApproved(Id, JobTitle, DepartmentId, Headcount, at));
    }

    public void Refused(string reason, DateTimeOffset at)
    {
        if (Status != RequisitionStatus.AwaitingApproval)
        {
            throw new InvalidOperationException(
                $"This is {Say(Status)}, so there is no approval outstanding on it.");
        }

        Status = RequisitionStatus.Refused;
        DecidedAt = at;
        Outcome = Require(reason, nameof(reason));

        Raise(new RequisitionRefused(Id, JobTitle, RaisedById, Outcome, at));
    }

    /// <summary>
    /// Somebody has been hired against this.
    /// </summary>
    /// <remarks>
    /// Closes the requisition when the last post is filled, so that nothing
    /// goes on being advertised after there is nobody left to hire.
    /// </remarks>
    public void RecordHire(DateTimeOffset at)
    {
        if (!CanHire)
        {
            throw new InvalidOperationException(
                Status == RequisitionStatus.Approved
                    ? $"All {Headcount} post(s) on this requisition are filled."
                    : $"This requisition is {Say(Status)} and cannot be hired against.");
        }

        HiredCount++;

        if (Remaining == 0)
        {
            Status = RequisitionStatus.Filled;
            DecidedAt = at;

            Raise(new RequisitionFilled(Id, JobTitle, Headcount, at));
        }
    }

    /// <summary>
    /// Change how many people are wanted.
    /// </summary>
    /// <remarks>
    /// Refused below the number already hired, because that count is a fact
    /// about people who now work here and no figure typed into a form can make
    /// it untrue.
    ///
    /// Raising it reopens a requisition that had filled. The system this
    /// replaces could not do that: a filled requisition stayed filled, so
    /// hiring a second person for the same role meant raising a fresh one and
    /// losing the approval that had already been given.
    /// </remarks>
    public void ChangeHeadcount(int headcount, DateTimeOffset at)
    {
        var wanted = Positive(headcount);

        if (wanted < HiredCount)
        {
            throw new InvalidOperationException(
                $"{HiredCount} people have already been hired against this, so the headcount "
                + $"cannot be set to {wanted}.");
        }

        if (wanted == Headcount)
        {
            return;
        }

        Headcount = wanted;

        if (Status == RequisitionStatus.Filled && Remaining > 0)
        {
            Status = RequisitionStatus.Approved;
            Outcome = null;
        }

        Raise(new RequisitionHeadcountChanged(Id, wanted, HiredCount, at));
    }

    /// <summary>
    /// Give up on it.
    /// </summary>
    /// <remarks>
    /// Kept rather than deleted. Somebody asks next year why a post was never
    /// filled, and the answer is on this row.
    /// </remarks>
    public void Close(string reason, DateTimeOffset at)
    {
        if (Status is RequisitionStatus.Closed or RequisitionStatus.Refused)
        {
            throw new InvalidOperationException($"This is already {Say(Status)}.");
        }

        Status = RequisitionStatus.Closed;
        DecidedAt = at;
        Outcome = Require(reason, nameof(reason));

        Raise(new RequisitionClosed(Id, JobTitle, Outcome, at));
    }

    public void Retitle(string jobTitle) => JobTitle = Require(jobTitle, nameof(jobTitle));

    public void Justify(string justification) =>
        Justification = Require(justification, nameof(justification));

    public void PlaceIn(Guid? departmentId) => DepartmentId = departmentId;

    /// <summary>Nothing here is a secret. The justification is read by approvers.</summary>
    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private static string Say(RequisitionStatus status) => status switch
    {
        RequisitionStatus.AwaitingApproval => "awaiting approval",
        _ => status.ToString().ToLowerInvariant(),
    };

    private static int Positive(int headcount) =>
        headcount < 1
            ? throw new ArgumentOutOfRangeException(
                nameof(headcount), headcount, "A requisition has to be for at least one person.")
            : headcount;

    private static string Require(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();
}
