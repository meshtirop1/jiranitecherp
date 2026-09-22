namespace JiranisokoTech.Domain.People;

/// <summary>
/// The rule that keeps the organisation a tree.
/// </summary>
/// <remarks>
/// A loop in the reporting lines — A answers to B, B to C, C to A — is not a
/// strange organisation, it is corrupt data. Nothing can draw it, "who approves
/// this?" never terminates, and the page that walks the chain hangs rather than
/// failing, which is the worst way for it to go wrong.
///
/// Written as a pure function over a lookup rather than as a service holding a
/// repository. The rule is about the shape of the graph and nothing else, so it
/// belongs in the domain and should be testable by handing it a dictionary.
/// </remarks>
public static class ReportingLine
{
    /// <summary>
    /// How far a chain may be followed before it is called broken.
    /// </summary>
    /// <remarks>
    /// A guard against data that is already looped when this is asked. The walk
    /// below would otherwise run forever on a cycle that predates the check,
    /// which is precisely the failure this exists to prevent.
    /// </remarks>
    private const int LongestSensibleChain = 200;

    /// <summary>
    /// Would making <paramref name="managerId"/> the manager of
    /// <paramref name="employeeId"/> close a loop?
    /// </summary>
    /// <param name="managerOf">
    /// Who somebody currently answers to, or null. A missing person is null too:
    /// an unknown id cannot be part of a loop.
    /// </param>
    public static bool WouldCycle(Guid employeeId, Guid? managerId, Func<Guid, Guid?> managerOf)
    {
        if (managerId is null)
        {
            // Answering to nobody is how the top of the firm is recorded, and it
            // cannot close anything.
            return false;
        }

        if (managerId == employeeId)
        {
            return true;
        }

        var seen = new HashSet<Guid> { employeeId };
        var walker = managerId;

        for (var step = 0; step < LongestSensibleChain; step++)
        {
            if (walker is not { } current)
            {
                // Reached the top without meeting ourselves.
                return false;
            }

            if (current == employeeId)
            {
                return true;
            }

            if (!seen.Add(current))
            {
                // A loop that was already there, further up the chain. Not the
                // one being asked about, but this is not the moment to add to it.
                return true;
            }

            walker = managerOf(current);
        }

        return true;
    }

    /// <summary>
    /// Everyone between somebody and the top of the firm, nearest first.
    /// </summary>
    /// <remarks>
    /// What an approval chain walks, and what a "who does this go to next?"
    /// question asks. It stops on a repeat rather than looping, so that data
    /// which is already broken produces a short answer instead of a hung page.
    /// </remarks>
    public static IReadOnlyList<Guid> ChainAbove(Guid employeeId, Func<Guid, Guid?> managerOf)
    {
        var chain = new List<Guid>();
        var seen = new HashSet<Guid> { employeeId };
        var walker = managerOf(employeeId);

        while (walker is { } current && seen.Add(current) && chain.Count < LongestSensibleChain)
        {
            chain.Add(current);
            walker = managerOf(current);
        }

        return chain;
    }
}
