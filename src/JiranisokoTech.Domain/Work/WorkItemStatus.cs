namespace JiranisokoTech.Domain.Work;

/// <summary>
/// Where a piece of work has got to.
/// </summary>
/// <remarks>
/// Named for what a person would say out loud, not for a workflow engine. The
/// list is deliberately short: every extra state is one more column on a board
/// that somebody has to decide between, and a board with nine columns is one
/// where everything sits in the middle three.
/// </remarks>
public enum WorkItemStatus
{
    /// <summary>Agreed, not started. Nobody need be on it yet.</summary>
    Todo = 1,

    /// <summary>Somebody is on it.</summary>
    InProgress = 2,

    /// <summary>Done by the person doing it, waiting on somebody else to look.</summary>
    InReview = 3,

    /// <summary>Stopped by something outside the work. Always carries a reason.</summary>
    Blocked = 4,

    /// <summary>Finished and accepted.</summary>
    Done = 5,

    /// <summary>Dropped. Kept, because "why did we not do that?" is a real question.</summary>
    Cancelled = 6,
}

/// <summary>
/// How much it matters, relative to everything else on the list.
/// </summary>
/// <remarks>
/// Four levels, with the top one named for what it actually means. Calling it
/// "critical" invites one per project; calling it what it is — everything else
/// stops — makes somebody think before choosing it.
/// </remarks>
public enum Priority
{
    Low = 1,
    Normal = 2,
    High = 3,

    /// <summary>Everything else stops until this is done.</summary>
    Dropping = 4,
}
