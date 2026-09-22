using JiranisokoTech.Application.Recruitment;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JiranisokoTech.Infrastructure.Recruitment;

public sealed class CvStoreOptions
{
    public const string Section = "Cvs";

    /// <summary>
    /// Where CVs are written. Relative paths are to the content root.
    /// </summary>
    /// <remarks>
    /// Deliberately not under wwwroot. Anything in wwwroot is served to anybody
    /// who guesses its address, and a folder of strangers' CVs published to the
    /// internet is the kind of mistake that ends up in a newspaper. These are
    /// read back through code that checks who is asking.
    /// </remarks>
    public string Directory { get; set; } = "cvs";
}

/// <summary>
/// CVs on disk, under names this system chose.
/// </summary>
/// <remarks>
/// The stored name is generated and has no relationship to what the candidate
/// called the file. That does three things at once: it stops one applicant
/// overwriting another's CV by both sending "cv.pdf"; it stops a name like
/// "../../appsettings.json" escaping the folder; and it means the name on disk
/// carries nothing about the person it belongs to.
///
/// The original name is kept on the application row, for whoever downloads it.
/// </remarks>
public sealed class FileCvStore(
    IOptions<CvStoreOptions> options,
    ILogger<FileCvStore> logger) : ICvStore
{
    public async Task<string> SaveAsync(
        Stream contents, string originalName, CancellationToken cancellationToken = default)
    {
        var folder = Root();

        Directory.CreateDirectory(folder);

        /*
         * The extension is taken from an allow-list rather than from the name.
         * GetExtension on a hostile name returns whatever was after the last
         * dot, and this is the one value from the upload that reaches the
         * filesystem.
         */
        var extension = Path.GetExtension(originalName);

        if (!Cv.Extensions.Contains(extension))
        {
            throw new InvalidOperationException(
                "That is not a document this system accepts.");
        }

        var stored = $"{Guid.CreateVersion7():N}{extension.ToLowerInvariant()}";

        await using var file = File.Create(Path.Combine(folder, stored));
        await contents.CopyToAsync(file, cancellationToken);

        logger.LogInformation("Stored a CV as {Stored}.", stored);

        return stored;
    }

    public Task<Stream?> OpenAsync(
        string storedName, CancellationToken cancellationToken = default)
    {
        var path = Resolve(storedName);

        return Task.FromResult<Stream?>(
            path is not null && File.Exists(path) ? File.OpenRead(path) : null);
    }

    public Task DeleteAsync(string storedName, CancellationToken cancellationToken = default)
    {
        var path = Resolve(storedName);

        if (path is not null && File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Turn a stored name into a path, or refuse.
    /// </summary>
    /// <remarks>
    /// Checked even though this system generated every name it will ever be
    /// given. The value arrives from a database column and travels through a
    /// query string on its way to being downloaded, and both are places
    /// somebody else can write. A path that leaves the folder is refused
    /// outright rather than cleaned up, because there is no legitimate reason
    /// for one.
    /// </remarks>
    private string? Resolve(string storedName)
    {
        if (string.IsNullOrWhiteSpace(storedName)
            || storedName.Contains('/')
            || storedName.Contains('\\')
            || storedName.Contains(".."))
        {
            logger.LogWarning("Refused a CV name that is not one of ours: {Name}.", storedName);

            return null;
        }

        var folder = Path.GetFullPath(Root());
        var path = Path.GetFullPath(Path.Combine(folder, storedName));

        return path.StartsWith(folder, StringComparison.Ordinal) ? path : null;
    }

    private string Root() => options.Value.Directory;
}
