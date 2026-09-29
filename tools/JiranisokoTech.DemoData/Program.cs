using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Infrastructure;
using JiranisokoTech.Infrastructure.Messaging;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace JiranisokoTech.DemoData;

/// <summary>
/// Fills a throwaway database with eighteen months of a small software firm.
/// </summary>
/// <remarks>
/// Section 83. The brief asks for development seed data covering an organisation, departments,
/// employees, projects, clients, tasks, repositories, invoices, candidates, job openings, assets,
/// servers and incidents — and adds one sentence that is harder than all of it: "Seed data must be
/// clearly identifiable as development/demo data."
///
/// That sentence is hard here because this is one firm's real ERP and the same binary runs in
/// production. There is no demo tier to put the data in and no environment variable that helps,
/// because a variable is a fact about where the tool ran and the row outlives the run. So the
/// marking is in the values — see <see cref="Marker"/> — and the guard against reaching a real
/// database is in this file.
///
/// <b>A tool rather than a page or a switch in the application.</b> A "fill with demo data" button
/// is a button somebody presses on the wrong copy, and a start-up flag is a flag somebody leaves
/// set. This has to be run deliberately, against a connection string typed at the time, by
/// somebody who has a shell on the machine. It is also not published into the runtime image: the
/// Dockerfile publishes only the web project.
///
/// The same shape as <c>tools/JiranisokoTech.ScaleCheck</c>, which is the precedent in this
/// repository for a tool that writes rows: its own environment variable rather than the
/// application's connection string, a refusal it prints as its whole output, and exit 2 for
/// "I did nothing".
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var connection = args.FirstOrDefault(one => !one.StartsWith("--", StringComparison.Ordinal))
            ?? Environment.GetEnvironmentVariable("DEMODATA_CONNECTION");

        if (string.IsNullOrWhiteSpace(connection))
        {
            Console.Error.WriteLine(
                """
                Give me a connection string, as an argument or in DEMODATA_CONNECTION.

                Point it at a throwaway database. This writes eighteen months of invented
                clients, people, invoices and incidents, and it refuses a database that already
                has any of them. See docs/demo-data.md.
                """);

            return 2;
        }

        var clock = new TravellingClock();

        var services = new ServiceCollection();

        /*
         * The connection string is passed in configuration rather than taken from the application's
         * own ConnectionStrings:Default, and that is the first half of the guard. A tool that read
         * the same key the application reads is a tool that fills whichever database happens to be
         * configured on the machine it is run on — which on a server is the live one.
         */
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = connection,

                // Every host, so the seed can connect a repository on more than one. See
                // Marker.GitSecret for why this is not a secret.
                ["Git:Providers:GitHub:Secret"] = Marker.GitSecret,
                ["Git:Providers:GitLab:Secret"] = Marker.GitSecret,
                ["Git:Providers:Bitbucket:Secret"] = Marker.GitSecret,
                ["Git:Providers:AzureDevOps:Secret"] = Marker.GitSecret,

                // Nothing this writes should try to send a letter.
                ["Mail:Transport"] = "None",
            })
            .Build();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddPersistence(configuration);
        services.AddMessaging(configuration);
        services.AddModules(configuration);

        /*
         * The mail registrations, which AddModules does not include — and without them twelve of
         * the queued announcements failed rather than settling.
         *
         * Several notice handlers take IMailer so that a notice can also be a letter, and the
         * handlers are resolved by the dispatcher through GetServices, so a missing registration is
         * not a start-up error: the message is claimed, the handler cannot be built, and it is
         * recorded as a failure and scheduled for retry. Twelve WorkItemAssigned messages did that
         * silently, and the demonstration database would have shown twelve dead letters on the
         * machinery page — which is precisely the thing that makes a demonstration look broken.
         *
         * Found by seeding a real database and counting what was left in the outbox. The transport
         * is None (see the configuration above), so this registers a mailer that writes nothing.
         */
        services.AddMail(configuration);

        /*
         * The travelling clock and the named actor replace what AddPersistence registered. Without
         * the first, every date would be the same three seconds; without the second, a year of
         * history would have no actor in the audit trail and nobody could explain it later.
         */
        services.RemoveAll<IClock>();
        services.AddSingleton<IClock>(clock);
        services.RemoveAll<ICurrentUser>();
        services.AddSingleton<ICurrentUser>(new DemoUser());

        /*
         * And sign-ins, which this tool has none of. The real implementation is over ASP.NET
         * Identity's UserManager, which would mean standing the whole identity stack up in a
         * console application for one method the seed never calls. See NoAccounts for why the stub
         * throws instead of doing nothing.
         */
        services.RemoveAll<Application.People.IAccountAccess>();
        services.AddSingleton<Application.People.IAccountAccess>(new NoAccounts());

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Console.WriteLine("Preparing the database.");
        await database.PrepareAsync(
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("demo-data"));

        if (await RefusedAsync(database) is { } why)
        {
            Console.Error.WriteLine(why);

            return 2;
        }

        Console.WriteLine("Writing eighteen months of a firm that does not exist.");

        var firm = new DemoFirm(scope.ServiceProvider, clock, Console.Out);
        var report = await firm.FillAsync();

        /*
         * The queue is drained as part of the seed rather than left for a running application to
         * find. Everything above raised domain events — a client taken on, an invoice sent, somebody
         * hired — and they are queued rather than dispatched, deliberately, so that a rule cannot
         * fire for something that then fails to save. Left queued, the first start of the
         * application after a seed would process eighteen months of them at once.
         *
         * See DemoFirm.DrainAsync for why it loops and what the left-over count is for.
         */
        var (settled, leftOver) = await firm.DrainAsync();

        if (leftOver > 0)
        {
            Console.Error.WriteLine(
                $"{leftOver} queued announcements are still waiting, which means they are failing "
                + "rather than queued. The application will keep retrying them; look at "
                + "/settings/machinery.");
        }

        Console.WriteLine();
        Console.WriteLine($"Done. {settled} queued announcements settled on the way out.");
        Console.WriteLine(
            $"  {report.Departments} departments, {report.People} people, "
            + $"{report.Clients} clients, {report.Projects} projects");
        Console.WriteLine(
            $"  {report.WorkItems} pieces of work, {report.Invoices} invoices, "
            + $"{report.Candidates} applications, {report.Assets} assets");
        Console.WriteLine(
            $"  {report.Resources} resources, {report.Incidents} incidents, "
            + $"{report.Tickets} tickets, {report.Repositories} repositories, "
            + $"{report.Articles} articles");
        Console.WriteLine();
        Console.WriteLine(
            $"Everything is marked. The firm calls itself \"{Marker.TradingName}\", every invoice "
            + $"number begins {Marker.InvoicePrefix}-, every code begins {Marker.Prefix}, every "
            + $"address ends at {Marker.EmailDomain}, and the audit trail says it was all done by "
            + $"{Marker.Actor}.");

        return 0;
    }

    /// <summary>
    /// Why this database must not be filled, or nothing.
    /// </summary>
    /// <remarks>
    /// Three refusals, and each covers a different way somebody gets this wrong.
    ///
    /// The first is the obvious one: a database with business records in it is somebody's, and the
    /// tool has nothing to add to it. Checked across four tables rather than one, because a
    /// half-migrated or half-restored copy may have clients and no invoices.
    ///
    /// The second is the one that would hurt. A firm that has ever sent an invoice is a firm whose
    /// trading name is on a document somebody outside it is holding — so overwriting that name with
    /// a marker, which is the very first thing the seed does, would change what the next reminder
    /// letter says about who is asking for money. <c>FirmSettings.IsReadyToInvoice</c> is the
    /// question the application itself asks before letting anybody invoice, so it is the right one
    /// to ask here.
    ///
    /// The third is for the case that looks safe: an empty database that somebody has already
    /// identified as the real firm. Nothing has been billed yet, so the second refusal passes —
    /// and the trading name is already the real one, which means somebody has set this copy up on
    /// purpose. A seed that overwrote it would be doing exactly what the second refusal exists to
    /// prevent, one day earlier.
    ///
    /// No prompt. A prompt in a tool nobody runs twice a year is one somebody answers without
    /// reading, and the refusals above are the check that a prompt pretends to be.
    /// </remarks>
    private static async Task<string?> RefusedAsync(AppDbContext database)
    {
        var records = new (string What, int Count)[]
        {
            ("clients", await database.Clients.CountAsync()),
            ("employees", await database.Employees.CountAsync()),
            ("projects", await database.Projects.CountAsync()),
            ("invoices", await database.Invoices.CountAsync()),
        };

        if (records.Where(one => one.Count > 0).ToList() is { Count: > 0 } found)
        {
            return "This database already has business records in it — "
                + string.Join(", ", found.Select(one => $"{one.Count} {one.What}"))
                + ". I will not add invented ones beside them. Point me at a throwaway database.";
        }

        var settings = await database.Set<Domain.Settings.FirmSettings>().FirstOrDefaultAsync();

        if (settings is null)
        {
            return null;
        }

        if (settings.IsReadyToInvoice)
        {
            return "This firm is set up to invoice, which means its real trading name is on "
                + "paper somebody outside it is holding. The first thing I do is overwrite that "
                + "name with a marker, so I am not going to.";
        }

        if (!settings.TradingName.Contains("DEMONSTRATION", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(settings.TradingName))
        {
            return $"This copy already calls itself \"{settings.TradingName}\", so somebody set it "
                + "up on purpose. Overwriting that with a marker is what I am here to avoid.";
        }

        return null;
    }
}
