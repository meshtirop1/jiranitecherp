namespace JiranisokoTech.Domain.Work;

/// <summary>
/// How big a piece of work is, and therefore what may sit under it.
/// </summary>
/// <remarks>
/// Section 11 asks for epics, features, stories and subtasks. They are one table with a kind
/// rather than four tables, and the reason is that they are the same thing at four sizes: each
/// has a title, an owner, a state and a place on a board, and four tables would mean four boards,
/// four state machines and four sets of permissions to keep in step.
///
/// The number is the depth, which is what makes the rule below expressible as a comparison
/// instead of a table of pairs: <b>a parent has to be bigger than its child</b>. Without that the
/// kinds are decoration — an epic under a subtask is allowed, and the words stop telling anybody
/// anything about the shape of the work.
///
/// <see cref="Task"/> sits between a story and a subtask because that is where the work this firm
/// actually raises falls: most of what goes on the board is a task with no story above it, and a
/// system that insisted on the whole ladder would have somebody inventing an epic called
/// "Miscellaneous" in the first week.
/// </remarks>
public enum WorkItemKind
{
    /// <summary>A body of work spanning months. Contains features.</summary>
    Epic = 1,

    /// <summary>Something the firm could describe to a client. Contains stories.</summary>
    Feature = 2,

    /// <summary>One thing somebody can use, described from their side.</summary>
    Story = 3,

    /// <summary>The ordinary card. What most work is.</summary>
    Task = 4,

    /// <summary>A step inside one card, for work worth splitting but not worth its own card.</summary>
    Subtask = 5,
}
