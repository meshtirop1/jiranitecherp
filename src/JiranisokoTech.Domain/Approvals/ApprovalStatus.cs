namespace JiranisokoTech.Domain.Approvals;

/// <summary>Where a request for a decision has got to.</summary>
public enum ApprovalStatus
{
    /// <summary>Waiting on somebody.</summary>
    Pending = 1,

    Approved = 2,

    /// <summary>Somebody said no, and said why.</summary>
    Refused = 3,

    /// <summary>Taken back by the person who asked, before anybody decided.</summary>
    Withdrawn = 4,
}

/// <summary>Where one step of a chain has got to.</summary>
public enum StepStatus
{
    /// <summary>Not yet reached, or reached and waiting.</summary>
    Waiting = 1,

    Approved = 2,

    Refused = 3,

    /// <summary>
    /// Passed over, with a reason.
    /// </summary>
    /// <remarks>
    /// For the case where the step cannot be decided at all — the post is
    /// vacant, the named person has left. It is not a quiet yes, and the reason
    /// is kept so that a chain which completed with a step skipped can be read
    /// honestly afterwards.
    /// </remarks>
    Skipped = 4,
}
