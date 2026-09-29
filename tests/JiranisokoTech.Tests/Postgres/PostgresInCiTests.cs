namespace JiranisokoTech.Tests.Postgres;

/// <summary>
/// Somewhere, the PostgreSQL tests actually run.
/// </summary>
/// <remarks>
/// They skip when <c>TEST_POSTGRES</c> is unset, which is right for the image build and for a
/// laptop without a database, and which also means a CI file that stopped providing one would
/// turn every one of them into a quiet skip while the run stayed green. This keeps the one
/// place that provides a server honest.
/// </remarks>
public class PostgresInCiTests
{
    [Fact]
    public void The_ci_workflow_provides_a_postgres_server_to_the_tests()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JiranisokoTech.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        var workflow = File.ReadAllText(Path.Combine(directory!.FullName, ".github", "workflows", "tests.yml"));

        Assert.Contains("image: postgres:", workflow);
        Assert.Contains("TEST_POSTGRES:", workflow);
        Assert.Contains("dotnet test", workflow);

        // And the image is built, which runs the suite against the trimmed source.
        Assert.Contains("docker build --file docker/Dockerfile", workflow);
    }
}
