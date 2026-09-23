using System.Diagnostics;
using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.Clients;
using JiranisokoTech.Infrastructure;
using JiranisokoTech.Infrastructure.Audit;
using JiranisokoTech.Infrastructure.Business;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Infrastructure.Reporting;
using JiranisokoTech.Infrastructure.Work;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace JiranisokoTech.ScaleCheck;

/// <summary>
/// Measures the read paths against a PostgreSQL database holding a realistic
/// quantity of rows, and says which of them the planner cannot answer from an index.
/// </summary>
/// <remarks>
/// Section 77 of the brief asks for performance, and the checklist said "indexed,
/// paged where it matters — untested at scale" for weeks. That sentence is an
/// admission rather than a status: every index in this system was added because
/// somebody reasoned about a query, and reasoning about a query planner is how a
/// schema ends up with six indexes nothing uses and none on the column that
/// matters.
///
/// So this asks PostgreSQL instead. It fills a throwaway database with more rows
/// than the firm will have for years, calls the same query methods the screens
/// call with the same arguments, and for every statement they issue reports how
/// long it took, how many statements it took, and whether the plan scans anything
/// large.
///
/// It is not a benchmark, and the timings are not comparable between machines. It
/// answers one question — is anything here unindexed or shaped as a query per row —
/// and that answer is the same on any machine.
/// </remarks>
internal static class Program
{
    /// <summary>
    /// How large a table has to be before a sequential scan over it is a finding.
    /// </summary>
    /// <remarks>
    /// Below this a scan is the right plan and an index would be slower, so flagging
    /// it would be noise — and a report full of noise is one whose real findings get
    /// skipped with the rest.
    /// </remarks>
    private const int ScanMattersAbove = 10_000;

    /// <summary>
    /// How often the same statement may be issued before it is a query per row.
    /// </summary>
    /// <remarks>
    /// The signature of the fault, and this took two attempts to get right. The first
    /// version counted statements and called anything past four a query per row —
    /// which flagged the dashboard, a screen that asks twelve different questions and
    /// issues twelve different statements to answer them. A fixed number of distinct
    /// statements is not the fault however large it is; the fault is <em>one</em>
    /// statement issued again and again with a different parameter, which is what this
    /// looks for.
    ///
    /// Twice, not once, because a query that reads a list and then reads one row for a
    /// header is two executions of nothing and three would be the first suspicious
    /// number.
    /// </remarks>
    private const int SameStatementCeiling = 2;

    /// <summary>
    /// How long one statement may take before it is worth naming.
    /// </summary>
    /// <remarks>
    /// A quarter of a second, measured by PostgreSQL rather than by the clock around
    /// the call, so it excludes the time spent materialising rows in the client. It is
    /// not a performance target — there is no sensible one that holds across machines
    /// — it is the threshold past which a statement is worth a person looking at.
    /// </remarks>
    private const double SlowMilliseconds = 250;

    /// <summary>
    /// How much of a query's time may be spent outside the database before it is worth
    /// naming separately.
    /// </summary>
    /// <remarks>
    /// Half a second, and it is reported apart from the statement time because the two have
    /// opposite fixes. A slow statement wants an index or a narrower query; a fast statement
    /// whose call took a second and a half wants fewer rows, because that second was spent
    /// turning them into objects. Reporting one number for both is how somebody spends an
    /// afternoon adding an index to a query that was already using one.
    /// </remarks>
    private const double BuildingMilliseconds = 500;

    /// <summary>How many times each query runs, to get past a cold cache.</summary>
    private const int Runs = 5;

    private static int _findings;

    private static async Task<int> Main(string[] args)
    {
        var connection = args.FirstOrDefault(one => !one.StartsWith("--", StringComparison.Ordinal))
            ?? Environment.GetEnvironmentVariable("SCALECHECK_CONNECTION");

        if (string.IsNullOrWhiteSpace(connection))
        {
            Console.Error.WriteLine(
                """
                Give me a connection string, as an argument or in SCALECHECK_CONNECTION.

                Point it at a throwaway database. This truncates thirteen tables before it
                starts and there is no confirmation prompt, because a prompt in a tool
                nobody runs twice a year is one somebody answers without reading. It does
                refuse a database that has any account in it. See docs/performance.md.
                """);

            return 2;
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connection)
            .AddInterceptors(Statements.Instance)
            .Options;

        await using var database = new AppDbContext(
            options, new SystemClock(), new UnattributedUser());

        Console.WriteLine("Applying migrations.");
        await database.Database.MigrateAsync();

        if (!args.Contains("--no-seed"))
        {
            await SeedAsync(database, connection);
        }

        await using var planner = new Planner(connection);
        await planner.OpenAsync();

        await ReportAsync(database, planner);

        return _findings == 0 ? 0 : 1;
    }

    /// <summary>
    /// Fills the database, refusing one that looks like it holds real work.
    /// </summary>
    /// <remarks>
    /// The guard is a row count on a table this tool never writes to. It is crude and
    /// it is the right crudeness: the failure being prevented is somebody pasting a
    /// production connection string into a tool whose first statement is TRUNCATE, and
    /// anything subtler would be something to argue with rather than a stop.
    /// </remarks>
    private static async Task SeedAsync(AppDbContext database, string connection)
    {
        var accounts = await database.Database
            .SqlQuery<long>($"""SELECT count(*) AS "Value" FROM "AspNetUsers" """)
            .SingleAsync();

        if (accounts > 0)
        {
            throw new InvalidOperationException(
                $"This database holds {accounts} account(s), so somebody signs in to it. The "
                + "scale check truncates thirteen tables. Point it somewhere else.");
        }

        var sql = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "volume.sql"));

        Console.WriteLine("Filling the tables. This takes a few minutes.");

        var clock = Stopwatch.StartNew();

        /*
         * Outside EF, and with a timeout measured in minutes. It is one script of bulk
         * inserts; the default command timeout is not enough for six hundred thousand
         * rows, and the failure it produces is a cancelled transaction three minutes
         * in that looks like the script being wrong.
         */
        await using var raw = new NpgsqlConnection(connection);
        await raw.OpenAsync();

        await using var command = raw.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 1800;
        await command.ExecuteNonQueryAsync();

        Console.WriteLine($"Filled in {clock.Elapsed.TotalSeconds:F0}s.");
    }

    private static async Task ReportAsync(AppDbContext database, Planner planner)
    {
        var business = new BusinessQueries(database, new SystemClock());
        var work = new WorkQueries(database);
        var audit = new AuditQueries(database);
        var money = new ProjectMoneyQueries(database);
        var reporting = new ReportingQueries(database);

        var anyClient = await database.Clients.Select(one => one.Id).FirstAsync();
        var anyEmployee = await database.Employees.Select(one => one.Id).FirstAsync();
        var anyProject = await database.Projects.Select(one => one.Id).FirstAsync();

        Console.WriteLine();
        Console.WriteLine("What is in it:");

        foreach (var (table, rows) in planner.Sizes
            .Where(one => one.Value > 1000)
            .OrderByDescending(one => one.Value)
            .Take(12))
        {
            Console.WriteLine($"  {table,-30} {rows,10:N0}");
        }

        Console.WriteLine();
        Console.WriteLine($"{"Query",-34}{"rows",9}{"stmts",7}{"median",10}");
        Console.WriteLine(new string('-', 60));

        var anyItem = await database.WorkItems.Select(one => one.Id).FirstAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // --- what a screen does ------------------------------------------------
        await Measure(planner, "clients list", () => business.ClientsAsync(),
            because: "what a client owes is summed by the Invoice aggregate, not in SQL "
                + "— one definition of what is owed. It costs reading every unpaid "
                + "invoice; see docs/performance.md");
        await Measure(planner, "pipeline, open", () => business.PipelineAsync());
        await Measure(planner, "pipeline, everything",
            () => business.PipelineAsync(includeClosed: true));
        await Measure(planner, "pipeline, one stage",
            () => business.PipelineAsync(Stage.Proposed));
        await Measure(planner, "contacts at a client", () => business.ContactsAsync(anyClient));
        await Measure(planner, "invoices, a page", () => business.InvoicesAsync(take: 50));
        await MeasureOne(planner, "invoices, the count", () => business.CountInvoicesAsync());
        await MeasureOne(planner, "invoices, what is owed",
            () => business.OutstandingAsync(today),
            because: "the same one definition, over the unpaid pile");
        await Measure(planner, "invoices, one client", () => business.InvoicesAsync(anyClient));
        await Measure(planner, "time, one person",
            () => business.TimeAsync(employeeId: anyEmployee));
        await Measure(planner, "the approval queue",
            () => business.TimeAsync(awaitingApprovalOnly: true, take: 201));
        await Measure(planner, "work items, a board",
            () => work.ItemsAsync(openOnly: true, take: 200),
            because: "open is 'not one of the domain's finished states', and a partial "
                + "index would put a second copy of that list in the schema — the exact "
                + "fault the query was written to avoid, for 120ms");
        await MeasureOne(planner, "one work item", () => work.ItemAsync(anyItem));
        await Measure(planner, "work items, one project",
            () => work.ItemsAsync(projectId: anyProject));
        await Measure(planner, "work items, one person",
            () => work.ItemsAsync(assigneeId: anyEmployee));
        await Measure(planner, "projects, running", () => work.ProjectsAsync(runningOnly: true));
        await MeasureOne(planner, "audit, first page", () => audit.RecentAsync());
        await MeasureOne(planner, "audit, filtered",
            () => audit.RecentAsync(subjectType: "Invoice"));
        await MeasureOne(planner, "audit, page 40", () => audit.RecentAsync(page: 40));

        /*
         * --- what is meant to read everything ----------------------------------
         *
         * These are named rather than left out. A scan is the right plan for a figure
         * computed over every row, and no index changes that — but a report that silently
         * skipped them would be a report nobody could tell was complete, and the next
         * person would add the index anyway to be sure.
         */
        await Measure(planner, "invoices, whole (export)", () => business.InvoicesAsync(),
            because: "the CSV export is the whole list by definition");
        await Measure(planner, "project money", () => money.AllAsync(),
            because: "margin per project is computed over every invoice, hour and claim");
        await MeasureOne(planner, "the firm's state", () => reporting.StateAsync(today),
            because: "a dashboard of twelve firm-wide figures reads the tables they are over");

        Console.WriteLine();

        Console.WriteLine(_findings == 0
            ? "Nothing scans a large table, and nothing issues a statement per row."
            : $"{_findings} finding(s) above. Each is a missing index or a query shaped wrongly.");
    }

    /// <param name="because">
    /// Why reading everything is right here, if it is. A row with a reason still reports
    /// what it did; it just does not count as a finding.
    /// </param>
    private static Task Measure<T>(
        Planner planner, string name, Func<Task<List<T>>> query, string? because = null) =>
        Run(planner, name, async () => (await query()).Count, because);

    private static Task MeasureOne<T>(
        Planner planner, string name, Func<Task<T>> query, string? because = null) =>
        Run(planner, name, async () =>
        {
            await query();
            return -1;
        }, because);

    /// <summary>
    /// Runs one query several times and reports the median rather than the mean.
    /// </summary>
    /// <remarks>
    /// The median, because the first run pays for a cold cache and an uncached plan,
    /// and a mean over five runs is mostly a measurement of that first one. What
    /// matters is what the fiftieth reader of a page waits for.
    /// </remarks>
    private static async Task Run(
        Planner planner, string name, Func<Task<int>> query, string? because)
    {
        var times = new List<double>(Runs);
        var rows = 0;

        for (var run = 0; run < Runs; run++)
        {
            Statements.Instance.Reset();

            var clock = Stopwatch.StartNew();
            rows = await query();
            clock.Stop();

            times.Add(clock.Elapsed.TotalMilliseconds);
        }

        var issued = Statements.Instance.Seen.ToList();

        times.Sort();

        var median = times[times.Count / 2];
        var shown = rows < 0 ? "—" : rows.ToString("N0");

        Console.WriteLine($"{name,-34}{shown,9}{issued.Count,7}{median,8:F1}ms");

        var repeated = issued
            .GroupBy(one => one.Sql)
            .Where(group => group.Count() > SameStatementCeiling)
            .ToList();

        var label = because is null ? "FINDING" : "expected";

        /*
         * A query per row is a finding whatever the reason given. Nothing legitimately
         * reads one row at a time in a loop, so an exemption for it would only ever be
         * used to silence the thing this was written to find.
         */
        foreach (var group in repeated)
        {
            Console.WriteLine(
                $"    FINDING: the same statement {group.Count()} times. That is a query per row.");

            _findings++;
        }

        var scanned = new List<string>();
        var slowest = 0d;

        // Grouped, because explaining the same statement forty times says nothing the
        // first one did not, and at volume it doubles how long this takes to run.
        foreach (var statement in issued.GroupBy(one => one.Sql).Select(group => group.First()))
        {
            var explained = await planner.ExplainAsync(statement, ScanMattersAbove);

            slowest = Math.Max(slowest, explained.Milliseconds);

            foreach (var table in explained.Scans.Where(one => !scanned.Contains(one)))
            {
                scanned.Add(table);
            }
        }

        foreach (var table in scanned)
        {
            Console.WriteLine($"    {label}: sequential scan over {table}.");

            if (because is null)
            {
                _findings++;
            }
        }

        if (slowest > SlowMilliseconds)
        {
            Console.WriteLine($"    {label}: slowest statement {slowest:F0}ms in the database.");

            if (because is null)
            {
                _findings++;
            }
        }

        /*
         * What the call took, less what the database took. At this point the rows are in
         * hand and the time is being spent building objects out of them — which is a
         * statement about how many rows were asked for, not about the schema.
         */
        var building = median - slowest;

        if (building > BuildingMilliseconds)
        {
            Console.WriteLine(
                $"    {label}: {building:F0}ms building rows into objects outside the database.");

            if (because is null)
            {
                _findings++;
            }
        }

        if (because is not null
            && (scanned.Count > 0 || slowest > SlowMilliseconds
                || building > BuildingMilliseconds))
        {
            Console.WriteLine($"             {because}.");
        }
    }
}
