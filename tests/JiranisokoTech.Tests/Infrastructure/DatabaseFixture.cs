using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Tests.Infrastructure;

/// <summary>
/// A real database for one test, then thrown away.
/// </summary>
/// <remarks>
/// SQLite in memory rather than EF's in-memory provider. The in-memory provider
/// is not a database: it has no schema, no constraints and no SQL, so it
/// cheerfully accepts writes that PostgreSQL would reject and proves nothing
/// about the mapping. SQLite runs the real migration path and the real queries.
///
/// The connection is held open deliberately — an in-memory SQLite database
/// exists only as long as a connection to it does, and closing it between
/// operations drops the schema mid-test.
/// </remarks>
public sealed class DatabaseFixture : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    private DatabaseFixture(SqliteConnection connection, TestClock clock, TestUser user)
    {
        _connection = connection;
        Clock = clock;
        User = user;
    }

    public TestClock Clock { get; }

    public TestUser User { get; }

    public static async Task<DatabaseFixture> CreateAsync(Guid? actorId = null, string? actorName = null)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var fixture = new DatabaseFixture(
            connection,
            new TestClock(),
            new TestUser(actorId, actorName));

        await using var context = fixture.NewContext();
        await context.Database.EnsureCreatedAsync();

        return fixture;
    }

    /// <summary>
    /// A fresh context over the same database.
    /// </summary>
    /// <remarks>
    /// Tests read back through a new context rather than the one that wrote, so
    /// an assertion cannot be satisfied by the change tracker holding the object
    /// it was just handed. What is asserted is what reached the database.
    /// </remarks>
    public TestDbContext NewContext() =>
        new(
            new DbContextOptionsBuilder<TestDbContext>().UseSqlite(_connection).Options,
            Clock,
            User);

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}

/// <summary>A clock the test moves, so date rules can be exercised on any day.</summary>
public sealed class TestClock : IClock
{
    public DateTimeOffset Now { get; set; } = new(2026, 9, 21, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The same day the interface computes, stated here so tests can reach it.
    /// </summary>
    /// <remarks>
    /// IClock.Today is a default interface member, which means it exists on the
    /// interface and not on this type. A test holding a TestClock could not see
    /// it without a cast, which is noise in every date-sensitive assertion.
    /// </remarks>
    public DateOnly Today => DateOnly.FromDateTime(Now.UtcDateTime);

    public void Advance(TimeSpan by) => Now = Now.Add(by);
}

public sealed class TestUser(Guid? id, string? name) : ICurrentUser
{
    public Guid? Id { get; set; } = id;

    public string? Name { get; set; } = name;
}
