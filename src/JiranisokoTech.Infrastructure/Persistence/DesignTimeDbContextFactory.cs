using JiranisokoTech.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace JiranisokoTech.Infrastructure.Persistence;

/// <summary>
/// Builds a context for the migration tools, and for nothing else.
/// </summary>
/// <remarks>
/// Migrations are provider-specific SQL, and this project runs on two providers:
/// PostgreSQL where it is deployed, SQLite on a laptop and in the suite. So the
/// migrations are generated for PostgreSQL — the one that holds data anybody
/// minds losing — and this factory exists to make that unambiguous. Without it
/// the tools would go through the application host, read whatever connection
/// string happened to be set, and cheerfully write SQLite migrations into the
/// folder that production reads.
///
/// It never connects. <c>migrations add</c> needs a provider to generate SQL
/// for, not a database to look at, so the placeholder below is enough. Applying
/// them uses the real connection string from configuration.
/// </remarks>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Host=localhost;Port=5432;Database=jiranisokotech;Username=design;Password=design";

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connection)
            .Options;

        // The clock and the actor are only ever read while saving, and nothing
        // is saved here.
        return new AppDbContext(options, new SystemClock(), new UnattributedUser());
    }
}
