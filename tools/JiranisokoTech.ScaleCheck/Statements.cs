using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace JiranisokoTech.ScaleCheck;

/// <summary>What PostgreSQL reported about one statement.</summary>
internal sealed record Plan(double Milliseconds, IReadOnlyList<string> Scans);

/// <summary>One statement the application issued, kept so it can be explained.</summary>
internal sealed record Statement(string Sql, IReadOnlyList<(string Name, object? Value)> Parameters);

/// <summary>
/// Records every statement a query issues.
/// </summary>
/// <remarks>
/// An interceptor rather than reading the log, for two reasons. The statement's
/// parameters come with it, and a parameterised query explained without its
/// parameters is explained against a plan the application never used — PostgreSQL
/// will happily choose a different one for an unknown value than for the value it
/// was given. And counting statements is the whole point: the failure that kills an
/// EF application at volume is not a slow query, it is a fast one issued four
/// hundred times, and that is invisible in a timing taken at the screen.
/// </remarks>
internal sealed class Statements : DbCommandInterceptor
{
    private readonly List<Statement> _seen = [];

    public static Statements Instance { get; } = new();

    public IReadOnlyList<Statement> Seen => _seen;

    public void Reset() => _seen.Clear();

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Record(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Record(command);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void Record(DbCommand command) =>
        _seen.Add(new Statement(
            command.CommandText,
            [.. command.Parameters
                .Cast<DbParameter>()
                .Select(one => (one.ParameterName, one.Value))]));
}

/// <summary>
/// Asks PostgreSQL what it actually did.
/// </summary>
/// <remarks>
/// The clock alone is not enough and this is why: a sequential scan over sixty
/// thousand rows is quick on a warm cache and a machine with nothing else to do.
/// The clock says the page is fine; the plan says it will not be at four times the
/// size, and four times is a year or two of an ERP nobody deletes from.
/// </remarks>
internal sealed class Planner(string connection) : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection = new(connection);

    private Dictionary<string, long> _sizes = [];

    public async Task OpenAsync()
    {
        await _connection.OpenAsync();

        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT relname, n_live_tup FROM pg_stat_user_tables;
            """;

        await using var reader = await command.ExecuteReaderAsync();

        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        while (await reader.ReadAsync())
        {
            sizes[reader.GetString(0)] = reader.GetInt64(1);
        }

        _sizes = sizes;
    }

    public IReadOnlyDictionary<string, long> Sizes => _sizes;

    /// <summary>
    /// What PostgreSQL did with one statement: how long it took inside the database,
    /// and which large tables it scanned to answer it.
    /// </summary>
    /// <remarks>
    /// Small tables are left out of the scans, and saying so is the point: a report
    /// that flagged every sequential scan would flag the settings row and the twelve
    /// departments, and teach whoever reads it to skip the findings with the rest.
    ///
    /// The time comes from EXPLAIN ANALYZE rather than from a clock around the call,
    /// so it is the database's own work and not the cost of turning forty thousand
    /// rows into objects. Both matter, and confusing them sends somebody adding an
    /// index to fix a query that is already using one.
    /// </remarks>
    public async Task<Plan> ExplainAsync(Statement statement, long mattersAbove)
    {
        /*
         * Statements EF issues for its own bookkeeping are skipped. EXPLAIN cannot
         * be prefixed onto a transaction control statement or a SET, and trying
         * throws rather than returning nothing useful.
         */
        if (!statement.Sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            return new Plan(0, []);
        }

        await using var command = _connection.CreateCommand();
        command.CommandText = "EXPLAIN (ANALYZE, FORMAT TEXT) " + statement.Sql;

        foreach (var (name, value) in statement.Parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var plan = new List<string>();

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            plan.Add(reader.GetString(0));
        }

        var found = new List<string>();
        var milliseconds = 0d;

        foreach (var line in plan)
        {
            if (line.StartsWith("Execution Time:", StringComparison.Ordinal)
                && double.TryParse(
                    line["Execution Time:".Length..].Replace("ms", string.Empty).Trim(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed))
            {
                milliseconds = parsed;
                continue;
            }

            var at = line.IndexOf("Seq Scan on ", StringComparison.Ordinal);

            if (at < 0)
            {
                continue;
            }

            var table = line[(at + "Seq Scan on ".Length)..].Split(' ')[0].Trim();

            if (_sizes.TryGetValue(table, out var rows)
                && rows > mattersAbove
                && !found.Any(one => one.StartsWith(table, StringComparison.Ordinal)))
            {
                found.Add($"{table} ({rows:N0} rows)");
            }
        }

        return new Plan(milliseconds, found);
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}
