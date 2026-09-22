namespace JiranisokoTech.Application.Recruitment;

/// <summary>
/// Where a candidate's CV is kept.
/// </summary>
/// <remarks>
/// An abstraction rather than a path, because the file is the one part of this
/// system that will move: disk today, object storage the day there is more than
/// one container. Nothing above this layer should have to know.
/// </remarks>
public interface ICvStore
{
    /// <summary>
    /// Keep a file, and say what to call it later.
    /// </summary>
    /// <returns>
    /// The name to store on the application. Never the name the candidate gave.
    /// </returns>
    Task<string> SaveAsync(
        Stream contents, string originalName, CancellationToken cancellationToken = default);

    /// <summary>Open a stored file, or null when it has gone.</summary>
    Task<Stream?> OpenAsync(string storedName, CancellationToken cancellationToken = default);

    Task DeleteAsync(string storedName, CancellationToken cancellationToken = default);
}

/// <summary>
/// What a CV may be.
/// </summary>
/// <remarks>
/// Both limits are here rather than in the page, because the page is not the
/// only thing that will ever write a CV and a limit enforced in one caller is a
/// limit somebody else forgets.
/// </remarks>
public static class Cv
{
    /// <summary>
    /// Five megabytes.
    /// </summary>
    /// <remarks>
    /// Generous for a document and small enough that a hundred applications do
    /// not fill a disk. Somebody sending a fifty-megabyte scan has made a
    /// mistake, and telling them so is more use than accepting it.
    /// </remarks>
    public const long MaximumBytes = 5 * 1024 * 1024;

    /// <summary>
    /// The formats that are accepted.
    /// </summary>
    /// <remarks>
    /// A short list, and an allow-list rather than a block-list. The point is
    /// not that these are safe to run — nothing here runs anything — but that a
    /// file which cannot be one of these is not a CV, and accepting it means
    /// storing whatever somebody felt like uploading.
    /// </remarks>
    public static IReadOnlySet<string> Extensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf", ".doc", ".docx", ".odt", ".rtf", ".txt",
        };

    /// <summary>Is this a name and size this system will take?</summary>
    public static bool Accepts(string fileName, long bytes, out string? why)
    {
        if (bytes <= 0)
        {
            why = "That file is empty.";

            return false;
        }

        if (bytes > MaximumBytes)
        {
            why = $"That file is larger than {MaximumBytes / 1024 / 1024}MB. "
                + "Send a smaller one, or a PDF rather than a scan.";

            return false;
        }

        var extension = Path.GetExtension(fileName);

        if (!Extensions.Contains(extension))
        {
            why = "That is not a document we can read. Send a PDF, a Word file, or plain text.";

            return false;
        }

        why = null;

        return true;
    }
}
