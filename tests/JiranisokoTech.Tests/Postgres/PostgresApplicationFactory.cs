using JiranisokoTech.Tests.Identity;
using Microsoft.AspNetCore.Hosting;
using Npgsql;

namespace JiranisokoTech.Tests.Postgres;

/// <summary>
/// The real application against a real PostgreSQL database, created for one test class and
/// dropped after it.
/// </summary>
/// <remarks>
/// <b>Why the suite needs this at all.</b> Everything else runs on SQLite in memory, which is
/// fast and needs nothing installed, and which is not the database production runs. The
/// security centre passed every test and was an error screen on PostgreSQL: the page rendered
/// once while its query was still in flight, and SQLite's queries finish before an await
/// yields, so that render never happened under test. Migrations are the other half — SQLite
/// builds its schema from the model, so a migration that does not apply on PostgreSQL fails
/// nothing here and everything on the first deploy. This factory runs the migrations, exactly
/// as startup does in production.
///
/// Opt-in through <c>TEST_POSTGRES</c>, a connection string to a server this user may create
/// databases on. The image build has no database to talk to, so there these tests are skipped
/// and say why; the CI workflow provides one, and so can anybody with PostgreSQL locally.
/// </remarks>
public class PostgresApplicationFactory : ApplicationFactory
{
    private readonly string _database = $"tests_{Guid.CreateVersion7():N}";

    private string? _connection;

    public static string? ServerConnection => Environment.GetEnvironmentVariable("TEST_POSTGRES");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        var server = ServerConnection ?? throw new InvalidOperationException(
            "TEST_POSTGRES is not set. These tests are marked [PostgresFact], which skips them "
            + "when it is not, so something is constructing this factory directly.");

        using (var connection = new NpgsqlConnection(server))
        {
            connection.Open();

            using var create = connection.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{_database}\"";
            create.ExecuteNonQuery();
        }

        _connection = new NpgsqlConnectionStringBuilder(server) { Database = _database }.ConnectionString;

        // Replaces the SQLite database the base class chose; the last setting of a key wins.
        builder.UseSetting("ConnectionStrings:Default", _connection);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing || _connection is null || ServerConnection is not { } server)
        {
            return;
        }

        // The application's pooled connections would otherwise hold the database open, and
        // a database somebody is connected to cannot be dropped.
        NpgsqlConnection.ClearAllPools();

        using var connection = new NpgsqlConnection(server);
        connection.Open();

        using var drop = connection.CreateCommand();
        drop.CommandText = $"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)";
        drop.ExecuteNonQuery();
    }
}

/// <summary>
/// A test that runs only when a PostgreSQL server has been provided.
/// </summary>
/// <remarks>
/// Skipped rather than passed when there is none, so the count at the end of a run says how
/// many did not run. A test that returned early would report green having checked nothing.
/// </remarks>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(PostgresApplicationFactory.ServerConnection))
        {
            Skip = "Set TEST_POSTGRES to a PostgreSQL connection string to run this against PostgreSQL.";
        }
    }
}
