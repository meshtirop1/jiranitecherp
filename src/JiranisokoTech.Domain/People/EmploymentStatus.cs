namespace JiranisokoTech.Domain.People;

/// <summary>
/// Where somebody stands with the firm.
/// </summary>
/// <remarks>
/// Four states rather than a boolean. "Active or not" cannot tell apart a person
/// who has been suspended this morning from one who left two years ago, and the
/// two need completely different handling: one keeps their work and may come
/// back on Monday, the other should disappear from every assignment list while
/// their name stays on everything they did.
/// </remarks>
public enum EmploymentStatus
{
    /// <summary>Offered and accepted, not started. Has a record, no work.</summary>
    Invited = 1,

    /// <summary>Working here.</summary>
    Active = 2,

    /// <summary>Still employed, temporarily stopped. Work stays theirs.</summary>
    Suspended = 3,

    /// <summary>Gone. Their name stays on their work; nothing new reaches them.</summary>
    Left = 4,
}
