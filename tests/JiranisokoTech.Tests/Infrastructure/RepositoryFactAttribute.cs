namespace JiranisokoTech.Tests.Infrastructure;

/// <summary>
/// A test of the repository's own files — compose, the proxy, the documents — which the image
/// build is deliberately not given.
/// </summary>
/// <remarks>
/// <b>These tests broke the image build, and nothing said so.</b> The suite runs inside the
/// Dockerfile, and <c>.dockerignore</c> leaves out <c>docker/</c> and <c>docs/</c> so that
/// editing a document does not throw away the build cache. The tests that check those files
/// were written after that rule and were only ever run from a checkout, where the files are
/// there. Inside the image they threw on the first missing file, the build stopped, and —
/// as CLAUDE.md warns — <c>compose up --build</c> went on serving the previous image. Found
/// by running the suite against a copy of the source trimmed the way the image sees it.
///
/// Skipped, and saying why, when the repository's files are not present — which happens only
/// inside the image build. Everywhere those files exist the tests run, and the CI workflow
/// runs them on every push; it also builds the image, which is the check that would have
/// caught this.
/// </remarks>
public sealed class RepositoryFactAttribute : FactAttribute
{
    public RepositoryFactAttribute()
    {
        if (!RepositoryFiles.Present)
        {
            Skip = RepositoryFiles.Absent;
        }
    }
}

/// <summary>The same, for a theory.</summary>
public sealed class RepositoryTheoryAttribute : TheoryAttribute
{
    public RepositoryTheoryAttribute()
    {
        if (!RepositoryFiles.Present)
        {
            Skip = RepositoryFiles.Absent;
        }
    }
}

/// <summary>
/// Where the repository is, found once.
/// </summary>
/// <remarks>
/// The walk below already had to know this to decide whether to skip, and nine test classes each
/// carry a private copy of the same loop. Two of those nine have already drifted -- they look for
/// <c>.env.example</c> rather than the solution file, and one of the two lost the null check its
/// siblings have, so a walk that finds nothing throws a NullReferenceException with no message
/// instead of saying what it was looking for. New tests take it from here.
/// </remarks>
internal static class Repository
{
    public static string Root { get; } = Find();

    private static string Find()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "JiranisokoTech.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            "No JiranisokoTech.slnx above " + AppContext.BaseDirectory
            + ", so this test cannot tell where the repository is.");
    }
}

internal static class RepositoryFiles
{
    public const string Absent =
        "The repository's docker/ and docs/ are not here — this is the image build, which is "
        + "not given them. CI runs this against the full checkout.";

    public static bool Present { get; } = Find();

    private static bool Find()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "JiranisokoTech.slnx")))
            {
                return File.Exists(Path.Combine(directory.FullName, "docker", "compose.yaml"))
                    && Directory.Exists(Path.Combine(directory.FullName, "docs"));
            }
        }

        return false;
    }
}
