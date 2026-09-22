using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JiranisokoTech.Infrastructure.Persistence;

/// <summary>
/// Getting the schema into the state the code expects, on the way up.
/// </summary>
public static class DatabaseStartup
{
    /// <summary>
    /// Apply migrations, or create the schema outright on a throwaway database.
    /// </summary>
    /// <remarks>
    /// Two providers, two answers, and the difference is deliberate.
    ///
    /// On PostgreSQL — the one that holds data anybody minds losing — migrations
    /// are applied. This replaces <c>EnsureCreated</c>, which creates a schema
    /// only when there is none at all and does precisely nothing to a database
    /// that already exists. Every column added after the first deployment would
    /// have been missing, and the application would have started cleanly and
    /// then failed on the first query that mentioned one.
    ///
    /// On SQLite — a laptop, the test suite — the schema is created from the
    /// model. Those databases are disposable, and the migrations are PostgreSQL
    /// SQL that SQLite could not run anyway. Generating a second set for SQLite
    /// would mean maintaining two, and the one that mattered would be the one
    /// nobody checked.
    ///
    /// Migrating on startup means a deployment cannot forget to. The trade is
    /// that two instances starting at once both try, and a migration that fails
    /// takes the application down rather than leaving it up and wrong. Both are
    /// the right way round for one container serving one firm; a larger
    /// deployment should run migrations as their own step and have the
    /// application only check.
    /// </remarks>
    public static async Task PrepareAsync(
        this AppDbContext database,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (database.Database.IsNpgsql())
        {
            var pending = (await database.Database.GetPendingMigrationsAsync(cancellationToken))
                .ToList();

            if (pending.Count > 0)
            {
                logger.LogInformation(
                    "Applying {Count} migration(s): {Migrations}",
                    pending.Count,
                    string.Join(", ", pending));
            }

            await database.Database.MigrateAsync(cancellationToken);

            return;
        }

        logger.LogInformation(
            "Creating the schema from the model. This database is not migrated and is "
            + "not meant to hold anything worth keeping.");

        await database.Database.EnsureCreatedAsync(cancellationToken);
    }
}
