namespace JiranisokoTech.Domain.Engineering;

/// <summary>
/// A version number, as a thing that can be ordered rather than a string.
/// </summary>
/// <remarks>
/// Section 67 asks for version numbers, and the reason this is a type instead of a column of
/// text is ordering. "What is live" and "what came after what" are the two questions a release
/// list exists to answer, and both are wrong under text ordering: <c>1.10.0</c> sorts before
/// <c>1.9.0</c>, and a page that says the firm is running 1.9 when it is running 1.10 is worse
/// than a page that says nothing.
///
/// <b>Three numbers are required, and a fourth part is optional.</b> Major, minor and patch,
/// with an optional prerelease tail after a hyphen. A leading <c>v</c> is accepted and dropped,
/// because people type it and because <c>v1.4.0</c> and <c>1.4.0</c> are the same release — two
/// rows for one version is the fault the unique index exists to prevent, and it would be
/// trivially reachable otherwise. A missing patch is read as zero, so <c>1.4</c> is <c>1.4.0</c>.
///
/// <b>Calendar versioning is not a special case.</b> A firm that releases as <c>2026.9.24</c>
/// parses here unchanged and orders correctly, which is why requiring the numeric form costs
/// nothing real. What it refuses is a name — "September release", "final-final" — and that
/// refusal is the point: a release nobody can order is a release nobody can roll back to.
///
/// <b>The prerelease tail orders the way SemVer says</b>, identifier by identifier, numbers
/// numerically and words lexically, and a prerelease is less than the same version without one.
/// Written out rather than compared as text because <c>rc.10</c> against <c>rc.9</c> is the
/// case a text comparison gets backwards, and a release candidate list is exactly where a
/// double-digit number shows up.
/// </remarks>
public readonly record struct ReleaseVersion : IComparable<ReleaseVersion>
{
    /// <summary>The longest a version's text form may be, matching the column.</summary>
    public const int MostCharacters = 60;

    public ReleaseVersion(int major, int minor, int patch, string? prerelease = null)
    {
        if (major < 0 || minor < 0 || patch < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(major), "A version's parts cannot be negative.");
        }

        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = string.IsNullOrWhiteSpace(prerelease) ? null : prerelease.Trim();
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    /// <summary>
    /// The tail after a hyphen, when there is one: <c>rc.1</c>, <c>beta</c>, <c>alpha.3</c>.
    /// </summary>
    /// <remarks>
    /// Null rather than empty when absent, because "no prerelease" and "a prerelease called
    /// nothing" order differently and only one of them exists.
    /// </remarks>
    public string? Prerelease { get; }

    /// <summary>Is this a release candidate rather than a release?</summary>
    public bool IsPrerelease => Prerelease is not null;

    /// <summary>
    /// Read a version somebody typed, or say why it cannot be read.
    /// </summary>
    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var said = text.Trim();

        if (said.Length > MostCharacters)
        {
            return false;
        }

        if (said.StartsWith('v') || said.StartsWith('V'))
        {
            said = said[1..];
        }

        var hyphen = said.IndexOf('-', StringComparison.Ordinal);
        var prerelease = hyphen < 0 ? null : said[(hyphen + 1)..];
        var numbers = (hyphen < 0 ? said : said[..hyphen]).Split('.');

        if (numbers.Length is < 2 or > 3 || (prerelease is not null && prerelease.Length == 0))
        {
            return false;
        }

        var parts = new int[3];

        for (var i = 0; i < numbers.Length; i++)
        {
            if (!int.TryParse(numbers[i], out parts[i]) || parts[i] < 0)
            {
                return false;
            }
        }

        version = new ReleaseVersion(parts[0], parts[1], parts[2], prerelease);

        return true;
    }

    /// <summary>
    /// Read a version, refusing anything that cannot be ordered.
    /// </summary>
    /// <remarks>
    /// The message names the shape rather than saying "invalid", because somebody who typed
    /// "September release" has to be told what would be accepted instead, and an error that
    /// only says no makes them try the same thing twice.
    /// </remarks>
    public static ReleaseVersion Parse(string? text) =>
        TryParse(text, out var version)
            ? version
            : throw new ArgumentException(
                "A version has to be numbers so that releases can be put in order — 1.4.0, "
                + "or 1.4.0-rc.1 for a candidate. A date works too: 2026.9.24.",
                nameof(text));

    public int CompareTo(ReleaseVersion other)
    {
        if (Major != other.Major)
        {
            return Major.CompareTo(other.Major);
        }

        if (Minor != other.Minor)
        {
            return Minor.CompareTo(other.Minor);
        }

        if (Patch != other.Patch)
        {
            return Patch.CompareTo(other.Patch);
        }

        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) =>
        left.CompareTo(right) < 0;

    public static bool operator >(ReleaseVersion left, ReleaseVersion right) =>
        left.CompareTo(right) > 0;

    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) =>
        left.CompareTo(right) <= 0;

    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) =>
        left.CompareTo(right) >= 0;

    public override string ToString() =>
        Prerelease is null
            ? $"{Major}.{Minor}.{Patch}"
            : $"{Major}.{Minor}.{Patch}-{Prerelease}";

    /// <summary>
    /// SemVer's rule for the tail, which is not the rule text comparison uses.
    /// </summary>
    /// <remarks>
    /// A version with no prerelease is greater than the same version with one — 1.4.0 comes
    /// after 1.4.0-rc.2, which is the whole reason candidates are numbered that way. Below
    /// that, identifiers are compared one at a time: both numeric compares numerically, so
    /// rc.9 precedes rc.10; anything else compares as text; and a numeric identifier is lower
    /// than a text one. A shorter run of identifiers wins when everything before it is equal,
    /// so rc precedes rc.1.
    /// </remarks>
    private static int ComparePrerelease(string? left, string? right)
    {
        if (left is null && right is null)
        {
            return 0;
        }

        if (left is null)
        {
            return 1;
        }

        if (right is null)
        {
            return -1;
        }

        var ours = left.Split('.');
        var theirs = right.Split('.');

        for (var i = 0; i < Math.Min(ours.Length, theirs.Length); i++)
        {
            var mineIsNumber = int.TryParse(ours[i], out var mine);
            var yoursIsNumber = int.TryParse(theirs[i], out var yours);

            var decided = (mineIsNumber, yoursIsNumber) switch
            {
                (true, true) => mine.CompareTo(yours),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(ours[i], theirs[i]),
            };

            if (decided != 0)
            {
                return decided;
            }
        }

        return ours.Length.CompareTo(theirs.Length);
    }
}
