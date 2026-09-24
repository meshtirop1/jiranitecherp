using System.Text.RegularExpressions;

namespace JiranisokoTech.Tests.Infrastructure;

/// <summary>
/// Which tables are swept, and which must never be.
/// </summary>
/// <remarks>
/// The second list is the one worth having. Three tables in this database exist so that
/// something can be proved later, and each of them would go on working perfectly for months
/// after a sweep quietly broke it:
///
/// <list type="bullet">
/// <item><c>audit_entries</c> is append-only, and enforced — except that the enforcement
/// inspects the change tracker, and <c>ExecuteDeleteAsync</c> never reaches the change
/// tracker. The guard in <c>AppDbContext.RefuseToRewriteHistory</c> is real for anything that
/// loads a row and real for nothing else.</item>
/// <item><c>webhook_deliveries</c> is the replay protection. The unique index on the
/// provider's own delivery id refuses a second arrival of the same delivery, and it protects
/// exactly the rows still in the table — so pruning it does not free space, it reopens the
/// window in which a replayed delivery is accepted as new.</item>
/// <item>Successful sign-ins are what <c>SignInPlaces</c> compares against to decide whether
/// to warn somebody their account has been used somewhere unfamiliar. Sweeping them switches
/// that warning off silently, per account, for ever.</item>
/// </list>
///
/// So this is a whitelist rather than a blacklist, and it fails closed: every place in the
/// source that deletes rows in bulk has to be named here, and a new one fails the build until
/// somebody has written down what it sweeps and why.
/// </remarks>
public class RetentionTests
{
    /// <summary>
    /// Every bulk delete in the source, and what it is allowed to sweep.
    /// </summary>
    /// <remarks>
    /// Named by file and by the set being deleted from, because both matter: the file says who
    /// decided, and the set says what they decided about. A call site that moves keeps its
    /// entry; a call site that changes what it deletes does not.
    /// </remarks>
    private static readonly Dictionary<string, string> Sweeps = new(StringComparer.Ordinal)
    {
        ["Messaging/OutboxDispatcher.cs"] =
            "Dispatched outbox messages older than the configured retention. Abandoned ones "
            + "are deliberately never swept — they are the ones somebody has to look at.",

        ["Scheduling/Jobs.cs"] =
            "Successful job runs older than ninety days, and failed sign-in attempts older "
            + "than ninety days. Failed job runs and successful sign-ins are both kept: the "
            + "first is the evidence of a fault, and the second is what decides whether a "
            + "sign-in is from somewhere new.",
    };

    /// <summary>
    /// Tables nothing may ever sweep, and the reason each one cannot be reconstructed.
    /// </summary>
    /// <remarks>
    /// Every spelling is listed rather than derived from the type name, and that is not
    /// pedantry — the first version of this test derived them, and the derivation produced
    /// "AuditEntrys" for a DbSet called <c>AuditEntries</c>. It passed, and it would have gone
    /// on passing over the exact line it was written to catch. A rule that computes what to
    /// look for is a rule nobody can check by reading.
    /// </remarks>
    private static readonly (string What, string[] Spellings, string Why)[] NeverSwept =
    [
        ("the audit trail",
            ["AuditEntries", "Set<AuditEntry>"],
            "The trail is append-only. Deleting from it is the thing it exists to prevent, and "
            + "RefuseToRewriteHistory inspects the change tracker, which a bulk delete never "
            + "reaches."),

        ("the delivery inbox",
            ["Deliveries", "Set<WebhookDelivery>"],
            "The unique index on the provider's own delivery id is the only replay protection "
            + "there is, and it protects exactly the rows still in the table — so pruning does "
            + "not free space, it reopens the window in which a replay is accepted as new."),
    ];

    /// <summary>
    /// No bulk delete exists that nobody has written a reason for.
    /// </summary>
    /// <remarks>
    /// A deletion is the one operation in this system that cannot be reviewed after the fact,
    /// because the evidence of what it did is what it removed. This is not a strong guard —
    /// it is a grep, and it is defeated by raw SQL, by a synchronous <c>ExecuteDelete</c>
    /// written without the suffix, by a sweep inside a migration, and by anything that deletes
    /// through the change tracker one row at a time. Those are all louder to write than the
    /// thing this catches, which is somebody adding a perfectly ordinary-looking sweep in the
    /// course of fixing something else.
    /// </remarks>
    [Fact]
    public void Every_bulk_delete_in_the_source_has_been_accounted_for()
    {
        var unaccounted = new List<string>();

        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);

            if (!text.Contains("ExecuteDelete", StringComparison.Ordinal))
            {
                continue;
            }

            var relative = Relative(file);

            if (!Sweeps.ContainsKey(relative))
            {
                unaccounted.Add($"  {relative}");
            }
        }

        Assert.True(
            unaccounted.Count == 0,
            "These files delete rows in bulk and nobody has said what they sweep:\n"
            + string.Join('\n', unaccounted.Order())
            + "\n\nAdd the file to RetentionTests.Sweeps with a sentence naming what goes and "
            + "what is deliberately kept. If it sweeps a table in NeverSwept, it is the sweep "
            + "that is wrong rather than the list.");
    }

    /// <summary>
    /// Nothing sweeps the two tables whose whole value is the rows still in them.
    /// </summary>
    /// <remarks>
    /// Matched on the DbSet property and on <c>Set&lt;T&gt;()</c> both, because this codebase
    /// uses whichever is to hand — <c>SignInRecord</c> has no DbSet property at all, so half
    /// the deletes here are written the second way. A check that knew only one spelling would
    /// walk straight past the other, which is exactly how somebody would write it.
    ///
    /// The spellings are listed rather than derived. An earlier version derived them and
    /// produced "AuditEntrys", so it passed while missing the one line in this repository it
    /// was written to catch; it was caught by deliberately adding that line and finding the
    /// test still green.
    /// </remarks>
    [Fact]
    public void Nothing_sweeps_a_table_that_cannot_be_reconstructed()
    {
        var offenders = new List<string>();

        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);

            foreach (var delete in Regex.Matches(text, @"ExecuteDelete(Async)?\s*\("))
            {
                var statement = StatementEndingAt(text, ((Match)delete).Index);

                foreach (var (what, spellings, why) in NeverSwept)
                {
                    if (spellings.Any(spelling => statement.Contains(spelling, StringComparison.Ordinal)))
                    {
                        offenders.Add($"  {Relative(file)} sweeps {what} — {why}");
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These are bulk deletes against tables whose value is the rows still in "
            + "them:\n" + string.Join('\n', offenders.Order()));
    }

    /// <summary>
    /// The statement the delete belongs to, read backwards to the previous semicolon.
    /// </summary>
    /// <remarks>
    /// Backwards because the interesting part is behind it. A bulk delete is written as a
    /// chain — the set, the filter, then the delete — so the name of the thing being swept is
    /// several lines above the call, and a forward read would find the next statement instead.
    /// </remarks>
    private static string StatementEndingAt(string text, int index)
    {
        var start = text.LastIndexOf(';', Math.Max(0, index - 1));

        return start < 0 ? text[..index] : text[(start + 1)..index];
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(Source(), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"));

    private static string Relative(string file)
    {
        var from = Source();
        var tail = file[(from.Length + 1)..];

        // Everything under src/<project>/, so an entry survives a project being renamed but
        // not a file moving between folders — which is the granularity a reason belongs at.
        var parts = tail.Split(Path.DirectorySeparatorChar);

        return string.Join('/', parts.Skip(1));
    }

    private static string Source()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return Path.Combine(directory!.FullName, "src");
    }
}
