using JiranisokoTech.Application.Business;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Infrastructure.Business;

namespace JiranisokoTech.Web.Reporting;

/// <summary>
/// Bringing a spreadsheet of clients into the system.
/// </summary>
/// <remarks>
/// Clients first because that is what somebody actually has on their first afternoon: three
/// years of client records in a spreadsheet, and no appetite for typing them in one at a
/// time.
///
/// <b>Nothing is written until the whole file has been checked.</b> This is the decision the
/// rest of the file serves. Importing row by row means an import that fails on row three
/// hundred has already committed two hundred and ninety-nine, and the person who ran it has
/// no way to know which — so their choices are to work out the difference by hand or to
/// import the whole file again and get four hundred duplicates. Checking first means the
/// answer is always either "nothing happened, here is what is wrong" or "all of it
/// happened".
///
/// <b>A dry run is the default on the screen.</b> Somebody importing four hundred clients
/// wants to see what would happen before it does, and an importer that only had one button
/// would be used once and distrusted afterwards.
///
/// <b>Duplicates within the file are found as well as against the database.</b> A
/// spreadsheet with the same client twice is the ordinary case — somebody appended a second
/// export to the first — and checking only against what is stored would let the first copy
/// in and then refuse the second, which is the worst of both answers.
/// </remarks>
public sealed class ClientImport(ClientService clients, BusinessQueries queries)
{
    public const string NameColumn = "name";
    public const string CodeColumn = "code";
    public const string ContactColumn = "contact";
    public const string EmailColumn = "email";

    /// <summary>
    /// The most rows one file may carry.
    /// </summary>
    /// <remarks>
    /// Two thousand. Not a technical limit — it is the point past which an import is a data
    /// migration that somebody should be watching a log for rather than a form submission
    /// waiting on a browser, and a request that takes four minutes is one a proxy will cut
    /// in the middle.
    /// </remarks>
    public const int Most = 2_000;

    /// <summary>Read a file, say what would happen, and optionally do it.</summary>
    public async Task<ImportOutcome> RunAsync(
        string text, bool commit, CancellationToken cancellationToken = default)
    {
        var file = CsvReader.Read(text);

        if (!file.IsReadable)
        {
            return ImportOutcome.Refused(file.Refusal!);
        }

        if (file.Missing(NameColumn) is { } missing)
        {
            return ImportOutcome.Refused(
                $"There is no '{missing}' column. The first line has to name the columns, and "
                + $"'{NameColumn}' is the only one that is required.");
        }

        if (file.Rows.Count == 0)
        {
            return ImportOutcome.Refused("There are no rows under the header.");
        }

        if (file.Rows.Count > Most)
        {
            return ImportOutcome.Refused(
                $"There are {file.Rows.Count} rows and the most this will take at once is "
                + $"{Most}. Split the file — an import that runs for minutes is one a proxy "
                + "cuts in the middle, and then nobody knows how much of it happened.");
        }

        var existing = await queries.ClientsAsync(cancellationToken: cancellationToken);

        var takenNames = existing
            .Select(client => client.Name.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var takenCodes = existing
            .Select(client => client.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var problems = new List<ImportProblem>();
        var planned = new List<PlannedClient>();

        var namesInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var codesInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in file.Rows)
        {
            if (row[NameColumn] is not { } name)
            {
                problems.Add(new ImportProblem(row.Line, "No name."));
                continue;
            }

            /*
             * The code is derived the same way the service derives it, so the duplicate check
             * here agrees with the one that will refuse it. Checking the raw text instead
             * would pass "Acme Ltd" and "acme ltd" as different and then fail on the second
             * once both had become the same slug.
             */
            var code = Slug.From(row[CodeColumn] ?? name).Value;

            if (takenNames.Contains(name))
            {
                problems.Add(new ImportProblem(
                    row.Line, $"'{name}' is already a client here."));

                continue;
            }

            if (takenCodes.Contains(code))
            {
                problems.Add(new ImportProblem(
                    row.Line, $"The code '{code}' is already in use."));

                continue;
            }

            if (!namesInFile.Add(name))
            {
                problems.Add(new ImportProblem(
                    row.Line, $"'{name}' appears more than once in this file."));

                continue;
            }

            if (!codesInFile.Add(code))
            {
                problems.Add(new ImportProblem(
                    row.Line, $"The code '{code}' is used twice in this file."));

                continue;
            }

            planned.Add(new PlannedClient(
                row.Line, name, code, row[ContactColumn], row[EmailColumn]));
        }

        /*
         * Any problem stops the whole thing, even when most rows are fine. A partial import
         * leaves somebody comparing a spreadsheet against a screen to find out what went in,
         * which is worse than having imported nothing — and the fix for a refusal is to
         * correct four cells and try again.
         */
        if (problems.Count > 0)
        {
            return ImportOutcome.WouldFail(planned, problems);
        }

        if (!commit)
        {
            return ImportOutcome.WouldWork(planned);
        }

        foreach (var client in planned)
        {
            await clients.TakeOnAsync(
                client.Name, client.Code, client.Contact, client.Email, cancellationToken);
        }

        return ImportOutcome.Done(planned);
    }
}

/// <summary>A client the file asks for.</summary>
public sealed record PlannedClient(
    int Line, string Name, string Code, string? Contact, string? Email);

/// <summary>Something wrong with one row, and where to find it.</summary>
public sealed record ImportProblem(int Line, string What);

/// <summary>
/// What the import would do, or did.
/// </summary>
/// <remarks>
/// Four states rather than a success flag and a list. "Would work", "would fail", "done" and
/// "refused" are four different sentences on the screen, and collapsing them into a boolean
/// means the page has to reconstruct which one it is from the shape of the other fields.
/// </remarks>
public sealed record ImportOutcome(
    ImportState State,
    IReadOnlyList<PlannedClient> Clients,
    IReadOnlyList<ImportProblem> Problems,
    string? Refusal)
{
    public static ImportOutcome Refused(string why) =>
        new(ImportState.Refused, [], [], why);

    public static ImportOutcome WouldFail(
        IReadOnlyList<PlannedClient> clients, IReadOnlyList<ImportProblem> problems) =>
        new(ImportState.WouldFail, clients, problems, null);

    public static ImportOutcome WouldWork(IReadOnlyList<PlannedClient> clients) =>
        new(ImportState.WouldWork, clients, [], null);

    public static ImportOutcome Done(IReadOnlyList<PlannedClient> clients) =>
        new(ImportState.Done, clients, [], null);
}

public enum ImportState
{
    /// <summary>The file itself could not be used at all.</summary>
    Refused = 1,

    /// <summary>Read, and something in it would stop the import.</summary>
    WouldFail = 2,

    /// <summary>Read, and nothing is wrong. Nothing has been written.</summary>
    WouldWork = 3,

    /// <summary>Written.</summary>
    Done = 4,
}
