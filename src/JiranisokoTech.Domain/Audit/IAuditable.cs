namespace JiranisokoTech.Domain.Audit;

/// <summary>
/// Marks an entity whose changes are worth recording.
/// </summary>
/// <remarks>
/// Opt-in, not automatic for everything. §29 asks that every *important*
/// mutation be audited, and the important word is important: auditing every
/// table indiscriminately fills the trail with join-table churn and cache rows,
/// and a trail nobody can read is one nobody checks.
///
/// Marking an entity is therefore a decision — this is a record a person will
/// one day have to answer for.
/// </remarks>
public interface IAuditable
{
    /// <summary>
    /// Columns never written to the trail, however much they change.
    /// </summary>
    /// <remarks>
    /// Password hashes, tokens, and anything else where the audit row would
    /// become a second copy of the secret, in a table deliberately readable by
    /// more people than the original.
    /// </remarks>
    static virtual IReadOnlySet<string> AuditExcludes => new HashSet<string>();
}
