using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Tests.Infrastructure;

/// <summary>
/// That the migrations still describe the model the code is written against.
/// </summary>
/// <remarks>
/// The failure this exists for is mundane and expensive: somebody adds a
/// property, the suite passes — because the test databases are built from the
/// model, not from the migrations — and the change reaches production as a
/// column that is not there. The application starts cleanly and falls over on
/// the first query that mentions it.
///
/// Nothing else in the suite can see that, precisely because everything else
/// runs on SQLite against a schema created from the model.
/// </remarks>
public class MigrationTests
{
    /// <summary>
    /// Built for PostgreSQL, because that is what the migrations are for.
    /// </summary>
    /// <remarks>
    /// The SQLite model is deliberately not the same — timestamps are stored as
    /// sortable text there, because SQLite refuses to sort a DateTimeOffset — so
    /// comparing a SQLite model against these migrations would report a
    /// difference on every single timestamp column, for ever.
    ///
    /// Nothing connects. The model and the migration snapshot are both built
    /// from code, and comparing them needs no database.
    /// </remarks>
    private static AppDbContext PostgresModel() =>
        new DesignTimeDbContextFactory().CreateDbContext([]);

    [Fact]
    public void Every_model_change_has_a_migration()
    {
        using var database = PostgresModel();

        Assert.False(
            database.Database.HasPendingModelChanges(),
            "The model has moved since the last migration. Run:\n"
            + "  dotnet ef migrations add <Name> "
            + "--project src/JiranisokoTech.Infrastructure "
            + "--startup-project src/JiranisokoTech.Infrastructure "
            + "--output-dir Persistence/Migrations");
    }

    /// <summary>
    /// There has to be at least one, or the check above passes by describing
    /// nothing.
    /// </summary>
    [Fact]
    public void There_are_migrations_to_apply()
    {
        using var database = PostgresModel();

        Assert.NotEmpty(database.Database.GetMigrations());
    }

    /// <summary>
    /// The tables the application cannot run without, named here so that losing
    /// one to a bad merge is a failing test rather than a broken deployment.
    /// </summary>
    [Theory]
    [InlineData("audit_entries")]
    [InlineData("outbox_messages")]
    [InlineData("sign_in_records")]
    [InlineData("AspNetUsers")]
    [InlineData("AspNetRoles")]
    [InlineData("AspNetRoleClaims")]
    public void The_schema_includes(string table)
    {
        using var database = PostgresModel();

        var tables = database.Model.GetEntityTypes()
            .Select(entity => entity.GetTableName())
            .ToHashSet();

        Assert.Contains(table, tables);
    }
}
