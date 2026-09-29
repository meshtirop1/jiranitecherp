using System.Diagnostics;
using System.Net;
using System.Text;
using JiranisokoTech.Domain.Clients;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Tests.Identity;
using JiranisokoTech.Tests.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace JiranisokoTech.Tests.Infrastructure;

/// <summary>
/// The backup is restored, and what comes back is what went in.
/// </summary>
/// <remarks>
/// Section 88 of the brief: "a backup that has never been tested for restoration should not be
/// considered reliable." So these run <c>docker/backup.sh</c> and <c>docker/restore.sh</c> —
/// the files the host runs, not a copy of their logic — against a real PostgreSQL, and then
/// look at the result: every table's contents compared with the source, the application
/// started on the restored database, and an account's second factor read back through the
/// restored key ring.
///
/// The key ring is the part most worth proving. It is backed up apart from the database, and
/// the two are only any use together: an authenticator key restored without the ring that
/// encrypted it reads as "no key", and every enrolled account is locked out of its second
/// factor. <see cref="Without_the_key_ring_the_restored_second_factors_are_unreadable"/> is
/// the evidence that the keys backup is not optional.
///
/// They need three things the image build does not have — the repository's docker/
/// directory, a PostgreSQL server and a POSIX shell with the PostgreSQL client — and skip,
/// saying which, without them. CI has all three. The scripts run here under whatever
/// <c>sh</c> is — dash on Ubuntu — and on a host under busybox in the postgres alpine image;
/// they are written to POSIX for that reason.
/// </remarks>
public sealed class BackupRestoreTests : IAsyncLifetime
{
    private readonly string _scratch =
        Path.Combine(Path.GetTempPath(), $"backup-tests-{Guid.CreateVersion7():N}");

    private readonly List<string> _databases = [];

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_scratch);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();

        if (PostgresApplicationFactory.ServerConnection is { } server)
        {
            await using var connection = new NpgsqlConnection(server);
            await connection.OpenAsync();

            foreach (var database in _databases)
            {
                await using var drop = connection.CreateCommand();
                drop.CommandText = $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)";
                await drop.ExecuteNonQueryAsync();
            }
        }

        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    /// <summary>
    /// Every row of every table, and every file, comes back exactly.
    /// </summary>
    [BackupFact]
    public async Task A_backup_restores_into_a_fresh_database_with_every_row_and_file()
    {
        var source = await MigratedDatabaseAsync();

        await using (var context = Context(source))
        {
            // A name with characters that a careless encoding would mangle, because the
            // firm's clients are in Nairobi and so are their names.
            context.Clients.Add(Client.TakeOn("Wanjiru & Njoroge — Ushirika", "wanjiru"));
            await context.SaveChangesAsync();
        }

        var files = Sources();
        File.WriteAllBytes(Path.Combine(files.Documents, "contracts", "signed.pdf"), [0, 1, 2, 255, 254, 13, 10]);
        File.WriteAllText(Path.Combine(files.Cvs, "applicant.docx"), "a CV");

        var (data, keys) = Backup(source, files);

        var restored = NewDatabaseName();
        var into = Targets();
        Restore(restored, into, data, keys);

        Assert.Equal(await EveryTableAsync(source), await EveryTableAsync(restored));

        await using (var context = Context(restored))
        {
            var client = await context.Clients.SingleAsync(each => each.Code == "wanjiru");
            Assert.Equal("Wanjiru & Njoroge — Ushirika", client.Name);

            // The application migrates on start. A restored database it thought was behind
            // would be migrated over the top of the restored data; one that is up to date is
            // simply opened.
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        }

        AssertSameFiles(files.Documents, into.Documents);
        AssertSameFiles(files.Cvs, into.Cvs);
        AssertSameFiles(files.Keys, into.Keys);
    }

    /// <summary>
    /// The application starts on what was restored, and an account's second factor still works.
    /// </summary>
    [BackupFact]
    public async Task The_application_starts_on_a_restore_and_reads_second_factors_through_the_restored_keys()
    {
        var files = Sources();
        string source;
        const string email = "restored@jiranisokotech.test";
        string? authenticator;

        await using (var running = new RunningApplication(files.Keys))
        {
            var user = await running.CreateAccountAsync(email, "a long enough password");

            await running.InScopeAsync(async services =>
            {
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var account = (await users.FindByIdAsync(user.Id.ToString()))!;
                await users.ResetAuthenticatorKeyAsync(account);
            });

            authenticator = await AuthenticatorKeyAsync(running, email);
            Assert.False(string.IsNullOrEmpty(authenticator));

            source = running.Database;
            var (data, keys) = Backup(source, files);

            var restored = NewDatabaseName();
            var into = Targets();
            Restore(restored, into, data, keys);

            await using var afterwards = new RestoredApplication(Connection(restored), into.Keys);

            var ready = await afterwards.CreateClient().GetAsync("/ready");
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

            Assert.Equal(authenticator, await AuthenticatorKeyAsync(afterwards, email));
        }
    }

    /// <summary>
    /// The database without its key ring: the account is there and its second factor is not.
    /// </summary>
    /// <remarks>
    /// This is what the separate keys backup is for, shown rather than asserted in a document.
    /// It is also why the two are never stored together: this test's other half is that
    /// somebody holding both can read every authenticator key in the firm.
    /// </remarks>
    [BackupFact]
    public async Task Without_the_key_ring_the_restored_second_factors_are_unreadable()
    {
        var files = Sources();
        const string email = "keyless@jiranisokotech.test";

        await using var running = new RunningApplication(files.Keys);

        var user = await running.CreateAccountAsync(email, "a long enough password");
        await running.InScopeAsync(async services =>
        {
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            await users.ResetAuthenticatorKeyAsync((await users.FindByIdAsync(user.Id.ToString()))!);
        });

        var (data, _) = Backup(running.Database, files);

        var restored = NewDatabaseName();
        var into = Targets();
        RunScript("restore.sh", Settings(restored, into), "--only", "database", data)
            .AssertSucceeded();

        // A fresh, empty key ring — which is what a host rebuilt without the keys backup has.
        await using var afterwards = new RestoredApplication(Connection(restored), into.Keys);

        Assert.Null(await AuthenticatorKeyAsync(afterwards, email));
    }

    /// <summary>
    /// A restore never writes over a database that holds something, unless told to.
    /// </summary>
    [BackupFact]
    public async Task Restore_refuses_a_database_that_is_not_empty_and_leaves_it_alone()
    {
        var source = await MigratedDatabaseAsync();
        await using (var context = Context(source))
        {
            context.Clients.Add(Client.TakeOn("Before the restore", "before"));
            await context.SaveChangesAsync();
        }

        var files = Sources();
        var (data, keys) = Backup(source, files);

        await using (var context = Context(source))
        {
            context.Clients.Add(Client.TakeOn("After the backup", "after"));
            await context.SaveChangesAsync();
        }

        var refused = RunScript("restore.sh", Settings(source, Targets()), data, keys);

        Assert.NotEqual(0, refused.ExitCode);
        Assert.Contains($"database {source} is not empty", refused.Output);

        await using (var context = Context(source))
        {
            Assert.True(await context.Clients.AnyAsync(each => each.Code == "after"));
        }

        // With --overwrite it is replaced — dropped and created, so the row written after the
        // backup is gone rather than merged with what the backup held.
        RunScript("restore.sh", Settings(source, Targets()), "--overwrite", data, keys).AssertSucceeded();

        // The drop ended every connection to it, including the ones pooled here.
        NpgsqlConnection.ClearAllPools();

        await using (var context = Context(source))
        {
            Assert.False(await context.Clients.AnyAsync(each => each.Code == "after"));
            Assert.True(await context.Clients.AnyAsync(each => each.Code == "before"));
        }
    }

    /// <summary>
    /// Nor over a directory that holds something.
    /// </summary>
    [BackupFact]
    public async Task Restore_refuses_a_directory_that_is_not_empty()
    {
        var source = await MigratedDatabaseAsync();
        var (data, keys) = Backup(source, Sources());

        var into = Targets();
        File.WriteAllText(Path.Combine(into.Documents, "somebody's.pdf"), "already here");

        var restored = NewDatabaseName();
        var refused = RunScript("restore.sh", Settings(restored, into), data, keys);

        Assert.NotEqual(0, refused.ExitCode);
        Assert.Contains($"{into.Documents} is not empty", refused.Output);

        // Refused before anything was done, so the database was not created either.
        Assert.False(await ExistsAsync(restored));
        Assert.Equal("already here", File.ReadAllText(Path.Combine(into.Documents, "somebody's.pdf")));
    }

    /// <summary>
    /// A damaged backup is refused before anything is touched.
    /// </summary>
    [BackupFact]
    public async Task Restore_refuses_a_backup_that_does_not_match_its_checksums()
    {
        var source = await MigratedDatabaseAsync();
        var (data, keys) = Backup(source, Sources());

        await using (var archive = File.Open(Path.Combine(data, "database.dump"), FileMode.Append))
        {
            archive.WriteByte(0);
        }

        var restored = NewDatabaseName();
        var refused = RunScript("restore.sh", Settings(restored, Targets()), data, keys);

        Assert.NotEqual(0, refused.ExitCode);
        Assert.Contains("does not match its checksums", refused.Output);
        Assert.False(await ExistsAsync(restored));
    }

    /// <summary>
    /// A backup that cannot reach the database fails, says so, and leaves nothing that looks
    /// like a backup behind.
    /// </summary>
    [BackupFact]
    public void A_backup_that_fails_exits_non_zero_and_leaves_no_partial_backup()
    {
        var files = Sources();
        var environment = Settings("a_database_that_does_not_exist", files);

        var failed = RunScript("backup.sh", environment);

        Assert.NotEqual(0, failed.ExitCode);
        Assert.Contains("backup FAILED", failed.Output);
        Assert.Empty(Directory.EnumerateFileSystemEntries(files.DataTarget));
        Assert.Empty(Directory.EnumerateFileSystemEntries(files.KeysTarget));
    }

    /// <summary>
    /// The keys are never written where the database is.
    /// </summary>
    /// <remarks>
    /// Checked by what is on disk rather than by comparing paths, because in a container the
    /// two destinations are always two different mount points and the host directories behind
    /// them can still be one.
    /// </remarks>
    [BackupTheory]
    [InlineData("same")]
    [InlineData("inside")]
    public async Task A_backup_refuses_to_put_the_keys_with_the_database(string arrangement)
    {
        var source = await MigratedDatabaseAsync();
        var files = Sources();
        var environment = Settings(source, files);

        environment["KEYS_BACKUP_TO"] = arrangement == "same"
            ? files.DataTarget
            : Path.Combine(files.DataTarget, "keys");

        var refused = RunScript("backup.sh", environment);

        Assert.NotEqual(0, refused.ExitCode);
        Assert.Contains("KEYS_BACKUP_TO and BACKUP_TO are the same place", refused.Output);
        Assert.False(Directory.EnumerateFiles(files.DataTarget, "*.dump", SearchOption.AllDirectories).Any());
    }

    /// <summary>
    /// Old backups go; the new one, and anything that is not a backup, stay.
    /// </summary>
    [BackupFact]
    public async Task Retention_removes_backups_older_than_the_setting_and_nothing_else()
    {
        var source = await MigratedDatabaseAsync();
        var files = Sources();

        var old = "20200101T000000Z";
        var recent = DateTime.UtcNow.AddDays(-2).ToString("yyyyMMdd'T'HHmmss'Z'");

        foreach (var target in new[] { files.DataTarget, files.KeysTarget })
        {
            Directory.CreateDirectory(Path.Combine(target, old));
            Directory.CreateDirectory(Path.Combine(target, recent));
        }

        Directory.CreateDirectory(Path.Combine(files.DataTarget, "kept by hand"));

        var (data, keys) = Backup(source, files, retentionDays: 30);

        foreach (var target in new[] { files.DataTarget, files.KeysTarget })
        {
            Assert.False(Directory.Exists(Path.Combine(target, old)), $"{old} survived in {target}");
            Assert.True(Directory.Exists(Path.Combine(target, recent)), $"{recent} was removed from {target}");
        }

        Assert.True(Directory.Exists(data));
        Assert.True(Directory.Exists(keys));
        Assert.True(Directory.Exists(Path.Combine(files.DataTarget, "kept by hand")));
    }

    // --- the scripts ---------------------------------------------------------------------

    private (string Data, string Keys) Backup(string database, Files files, int retentionDays = 0)
    {
        var environment = Settings(database, files);
        environment["RETENTION_DAYS"] = retentionDays.ToString();

        var before = Directory.GetDirectories(files.DataTarget).ToHashSet();

        RunScript("backup.sh", environment).AssertSucceeded();

        var written = Directory.GetDirectories(files.DataTarget).Except(before).ToList();
        var data = Assert.Single(written);
        var keys = Path.Combine(files.KeysTarget, Path.GetFileName(data));

        // The separation, as it lands on disk: the keys are in one place only, and it is not
        // the place the database went.
        Assert.True(File.Exists(Path.Combine(data, "database.dump")));
        Assert.False(File.Exists(Path.Combine(data, "keys.tar.gz")));
        Assert.True(File.Exists(Path.Combine(keys, "keys.tar.gz")));
        Assert.False(File.Exists(Path.Combine(keys, "database.dump")));

        // And nothing half-written was left for a person or the retention to find.
        Assert.Empty(Directory.GetDirectories(files.DataTarget, ".*.partial"));

        return (data, keys);
    }

    private void Restore(string database, Files into, string data, string keys) =>
        RunScript("restore.sh", Settings(database, into), data, keys).AssertSucceeded();

    private static Result RunScript(string script, Dictionary<string, string?> environment, params string[] arguments)
    {
        var start = new ProcessStartInfo("sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        start.ArgumentList.Add(Path.Combine(RepositoryRoot(), "docker", script));

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // Nothing inherited from the machine running the tests decides which database is
        // dumped or dropped: every setting the scripts read is set here, or removed.
        foreach (var name in new[] { "PGHOST", "PGPORT", "PGUSER", "PGPASSWORD", "PGDATABASE", "PGSERVICE", "PGPASSFILE" })
        {
            start.Environment.Remove(name);
        }

        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start)!;

        var output = new StringBuilder();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{script} did not finish within two minutes.");
        }

        output.Append(standardOutput.Result).Append(standardError.Result);

        return new Result(script, process.ExitCode, output.ToString());
    }

    private sealed record Result(string Script, int ExitCode, string Output)
    {
        public void AssertSucceeded() =>
            Assert.True(ExitCode == 0, $"{Script} exited {ExitCode}:\n{Output}");
    }

    private static Dictionary<string, string?> Settings(string database, Files files)
    {
        var server = new NpgsqlConnectionStringBuilder(PostgresApplicationFactory.ServerConnection);

        return new Dictionary<string, string?>
        {
            ["PGHOST"] = server.Host,
            ["PGPORT"] = server.Port.ToString(),
            ["PGUSER"] = server.Username,
            ["PGPASSWORD"] = server.Password,
            ["PGDATABASE"] = database,
            ["MAINTENANCE_DATABASE"] = server.Database ?? "postgres",
            ["BACKUP_TO"] = files.DataTarget,
            ["KEYS_BACKUP_TO"] = files.KeysTarget,
            ["DOCUMENTS_FROM"] = files.Documents,
            ["CVS_FROM"] = files.Cvs,
            ["KEYS_FROM"] = files.Keys,
            ["DOCUMENTS_TO"] = files.Documents,
            ["CVS_TO"] = files.Cvs,
            ["KEYS_TO"] = files.Keys,
        };
    }

    // --- stand-ins for the volumes -------------------------------------------------------

    /// <summary>
    /// The three volumes and the two backup destinations, as directories.
    /// </summary>
    private sealed record Files(string Documents, string Cvs, string Keys, string DataTarget, string KeysTarget);

    private Files Sources()
    {
        var root = Path.Combine(_scratch, $"source-{Guid.CreateVersion7():N}");
        var files = new Files(
            Path.Combine(root, "documents"),
            Path.Combine(root, "cvs"),
            Path.Combine(root, "keys"),
            Path.Combine(root, "backups", "data"),
            Path.Combine(root, "backups-keys"));

        Directory.CreateDirectory(Path.Combine(files.Documents, "contracts"));
        Directory.CreateDirectory(files.Cvs);
        Directory.CreateDirectory(files.Keys);
        Directory.CreateDirectory(files.DataTarget);
        Directory.CreateDirectory(files.KeysTarget);

        // The backup refuses an empty key ring as the wrong directory mounted, so there is
        // always something here. Not an .xml file: the application reads every one in the
        // directory as a key, and the tests that start it write their real ones alongside.
        File.WriteAllText(Path.Combine(files.Keys, "placeholder"), "not a key");

        return files;
    }

    private Files Targets()
    {
        var root = Path.Combine(_scratch, $"restored-{Guid.CreateVersion7():N}");
        var files = new Files(
            Path.Combine(root, "documents"),
            Path.Combine(root, "cvs"),
            Path.Combine(root, "keys"),
            Path.Combine(root, "unused"),
            Path.Combine(root, "unused-keys"));

        Directory.CreateDirectory(files.Documents);
        Directory.CreateDirectory(files.Cvs);
        Directory.CreateDirectory(files.Keys);

        return files;
    }

    private static void AssertSameFiles(string expected, string actual)
    {
        static Dictionary<string, byte[]> Read(string root) =>
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(root, path), File.ReadAllBytes);

        var want = Read(expected);
        var got = Read(actual);

        Assert.Equal(want.Keys.Order(), got.Keys.Order());

        foreach (var (path, bytes) in want)
        {
            Assert.True(bytes.SequenceEqual(got[path]), $"{path} came back different.");
        }
    }

    // --- databases -----------------------------------------------------------------------

    private string NewDatabaseName()
    {
        var name = $"tests_backup_{Guid.CreateVersion7():N}";
        _databases.Add(name);
        return name;
    }

    private async Task<string> MigratedDatabaseAsync()
    {
        var name = NewDatabaseName();

        await using (var server = new NpgsqlConnection(PostgresApplicationFactory.ServerConnection))
        {
            await server.OpenAsync();
            await using var create = server.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{name}\"";
            await create.ExecuteNonQueryAsync();
        }

        // The application's own migrations, as startup runs them — so the dump holds the real
        // schema, sequences and history table rather than something drawn up for the test.
        await using var context = Context(name);
        await context.Database.MigrateAsync();

        return name;
    }

    private static AppDbContext Context(string database) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(Connection(database)).Options,
            new TestClock(),
            new TestUser(null, "backup tests"));

    private static string Connection(string database) =>
        new NpgsqlConnectionStringBuilder(PostgresApplicationFactory.ServerConnection) { Database = database }
            .ConnectionString;

    private static async Task<bool> ExistsAsync(string database)
    {
        await using var server = new NpgsqlConnection(PostgresApplicationFactory.ServerConnection);
        await server.OpenAsync();
        await using var query = server.CreateCommand();
        query.CommandText = "SELECT count(*) FROM pg_database WHERE datname = @name";
        query.Parameters.AddWithValue("name", database);
        return (long)(await query.ExecuteScalarAsync())! > 0;
    }

    /// <summary>
    /// Every table in the database, each as a hash of all its rows in a fixed order.
    /// </summary>
    /// <remarks>
    /// Whole rows, not a count and not a chosen few columns, so a restore that dropped a column's
    /// values, changed a timestamp's zone or mangled a name's encoding is a difference here. The
    /// sequences are included, because a restore that brought the rows back and reset a sequence
    /// fails on the first insert with a duplicate key.
    /// </remarks>
    private static async Task<SortedDictionary<string, string>> EveryTableAsync(string database)
    {
        NpgsqlConnection.ClearAllPools();

        await using var connection = new NpgsqlConnection(Connection(database));
        await connection.OpenAsync();

        var tables = new List<string>();
        await using (var list = connection.CreateCommand())
        {
            list.CommandText = """
                SELECT format('%I.%I', schemaname, tablename) FROM pg_tables
                WHERE schemaname NOT IN ('pg_catalog', 'information_schema')
                """;
            await using var reader = await list.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
        }

        Assert.NotEmpty(tables);

        var contents = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var table in tables)
        {
            await using var hash = connection.CreateCommand();
            hash.CommandText =
                $"SELECT count(*) || ':' || coalesce(md5(string_agg(t::text, E'\\n' ORDER BY t::text)), '') FROM {table} t";
            contents[table] = (string)(await hash.ExecuteScalarAsync())!;
        }

        await using (var sequences = connection.CreateCommand())
        {
            sequences.CommandText = """
                SELECT format('%I.%I', schemaname, sequencename), coalesce(last_value::text, 'unused')
                FROM pg_sequences WHERE schemaname NOT IN ('pg_catalog', 'information_schema')
                """;
            await using var reader = await sequences.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                contents["sequence " + reader.GetString(0)] = reader.GetString(1);
            }
        }

        return contents;
    }

    private static async Task<string?> AuthenticatorKeyAsync(ApplicationFactory application, string email)
    {
        string? key = null;

        await application.InScopeAsync(async services =>
        {
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var account = await users.FindByEmailAsync(email);
            Assert.NotNull(account);
            key = await users.GetAuthenticatorKeyAsync(account!);
        });

        return key;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JiranisokoTech.slnx")))
        {
            directory = directory.Parent;
        }

        return directory!.FullName;
    }

    /// <summary>
    /// The application on its own new PostgreSQL database, with its key ring in a directory —
    /// as a host runs it, where the keys are on a volume.
    /// </summary>
    private sealed class RunningApplication(string keys) : PostgresApplicationFactory
    {
        public string Database =>
            new NpgsqlConnectionStringBuilder(
                Services.GetRequiredService<IConfiguration>()["ConnectionStrings:Default"]).Database!;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            Quiet(builder);
            builder.UseSetting("DataProtection:KeyRingPath", keys);
        }
    }

    /// <summary>
    /// The application started on a restored database and key ring — the second half of a
    /// recovery, which is where a restore that only looked complete would show it.
    /// </summary>
    private sealed class RestoredApplication(string connection, string keys) : ApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            Quiet(builder);
            builder.UseSetting("ConnectionStrings:Default", connection);
            builder.UseSetting("DataProtection:KeyRingPath", keys);
        }
    }

    /// <summary>
    /// The background loops slowed to an hour and mail discarded: they would otherwise be
    /// writing to the database while it is dumped, which is fine for a backup and makes a
    /// test's "what went in" a moving target.
    /// </summary>
    private static void Quiet(IWebHostBuilder builder)
    {
        builder.UseSetting("Git:PollInterval", "01:00:00");
        builder.UseSetting("Outbox:PollInterval", "01:00:00");
        builder.UseSetting("Mail:Transport", "None");
    }
}

/// <summary>
/// A test that runs the backup scripts: it needs the repository's docker/ directory, a
/// PostgreSQL server, and a POSIX shell.
/// </summary>
/// <remarks>
/// Skipped, saying which is missing, rather than passed. The image build has neither the
/// scripts nor a server; a Windows machine has no <c>sh</c> to run them with, and the host
/// they are written for is a Linux container.
/// </remarks>
public sealed class BackupFactAttribute : FactAttribute
{
    public BackupFactAttribute() => Skip = BackupTests.Unavailable;
}

/// <summary>The same, for a theory.</summary>
public sealed class BackupTheoryAttribute : TheoryAttribute
{
    public BackupTheoryAttribute() => Skip = BackupTests.Unavailable;
}

internal static class BackupTests
{
    public static string? Unavailable =>
        !RepositoryFiles.Present ? RepositoryFiles.Absent
        : string.IsNullOrWhiteSpace(PostgresApplicationFactory.ServerConnection)
            ? "Set TEST_POSTGRES to a PostgreSQL connection string to run the backup scripts against PostgreSQL."
        : OperatingSystem.IsWindows()
            ? "The backup scripts are POSIX sh, run in a Linux container; there is no sh here to run them with."
        : null;
}
