using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.People;
using JiranisokoTech.DemoData;
using JiranisokoTech.Infrastructure;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JiranisokoTech.Tests.Infrastructure;

/// <summary>
/// The demonstration seed, and whether it is still obviously a demonstration.
/// </summary>
/// <remarks>
/// Section 83, whose last sentence is the hard part: "Seed data must be clearly identifiable as
/// development/demo data." This is one firm's real ERP and the same binary runs in production, so
/// there is no demo tier to put the data in — the marking is in the values, and a marker that has
/// quietly stopped being applied is worse than none, because the data then looks real and somebody
/// has been told it is marked.
///
/// So these walk the database the seed wrote and insist on the markers, rather than asserting that
/// the tool ran. The tool running is the easy part; the eighteen months of services it calls are
/// checked by it completing at all, which is why one test here is the widest integration check in
/// the repository.
///
/// Against SQLite, like every other fixture here. The tool is used against PostgreSQL and the
/// difference does not touch what is being asserted — every marker is a string in a column.
/// </remarks>
public class DemoDataTests
{
    /// <summary>
    /// <b>Every row the seed wrote says it is not real.</b>
    /// </summary>
    /// <remarks>
    /// Five markers, checked in the five places somebody meets the data: the firm's own name, the
    /// invoice numbers, the codes and slugs, the e-mail addresses and the audit trail. Any one
    /// alone leaves a route by which a demonstration row looks real, which is why all five are
    /// asserted rather than the one that is easiest to check.
    /// </remarks>
    [Fact]
    public async Task Everything_the_seed_writes_says_it_is_not_real()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var (report, database) = await SeedAsync(fixture);

        // The firm itself, which is what reaches an invoice and an offer letter.
        var settings = await database.Set<JiranisokoTech.Domain.Settings.FirmSettings>().SingleAsync();

        Assert.Contains("DEMONSTRATION", settings.TradingName);
        Assert.Contains("DEMONSTRATION", settings.LegalName!);
        Assert.Equal(Marker.InvoicePrefix, settings.InvoicePrefix);

        // Every invoice number, which is the one marker that leaves this application.
        var invoices = await database.Invoices.AsNoTracking().Select(one => one.Number).ToListAsync();

        Assert.NotEmpty(invoices);
        Assert.All(invoices, number => Assert.StartsWith(Marker.InvoicePrefix, number));

        // Every code, slug and tag, which is what somebody querying the database sees.
        Assert.All(
            await database.Clients.AsNoTracking().Select(one => one.Code).ToListAsync(),
            code => Assert.StartsWith(Marker.Prefix, code));

        Assert.All(
            await database.Projects.AsNoTracking()
                .Where(one => one.Code != null).Select(one => one.Code!).ToListAsync(),
            code => Assert.StartsWith(Marker.Prefix, code));

        Assert.All(
            await database.Departments.AsNoTracking().Select(one => one.Slug).ToListAsync(),
            slug => Assert.StartsWith(Marker.Prefix, slug));

        Assert.All(
            await database.Assets.AsNoTracking().Select(one => one.Tag).ToListAsync(),
            tag => Assert.StartsWith(Marker.Prefix.ToUpperInvariant(), tag));

        // Every address, at a domain RFC 2606 guarantees cannot receive anything.
        var addresses = await database.Contacts.AsNoTracking()
            .Where(one => one.Email != null)
            .Select(one => one.Email!)
            .ToListAsync();

        Assert.NotEmpty(addresses);
        Assert.All(addresses, email => Assert.EndsWith(Marker.EmailDomain, email));

        Assert.All(
            await database.Candidates.AsNoTracking().Select(one => one.Email).ToListAsync(),
            email => Assert.EndsWith(Marker.EmailDomain, email));

        /*
         * And the audit trail, which is the marker that answers the question somebody asks a year
         * later. Not "who was this" but "why is there a year of history nobody remembers making".
         */
        var actors = await database.AuditEntries.AsNoTracking()
            .Where(one => one.ActorName != null)
            .Select(one => one.ActorName!)
            .Distinct()
            .ToListAsync();

        Assert.NotEmpty(actors);
        Assert.All(actors, actor => Assert.Equal(Marker.Actor, actor));

        // And it wrote everything the brief's list asks for.
        Assert.True(report.Departments >= 3, "departments");
        Assert.True(report.People >= 8, "people");
        Assert.True(report.Clients >= 3, "clients");
        Assert.True(report.Projects >= 3, "projects");
        Assert.True(report.WorkItems >= 10, "work items");
        Assert.True(report.Invoices >= 4, "invoices");
        Assert.True(report.Candidates >= 4, "candidates");
        Assert.True(report.Assets >= 6, "assets");
        Assert.True(report.Resources >= 4, "resources");
        Assert.True(report.Incidents >= 2, "incidents");
        Assert.True(report.Repositories >= 2, "repositories");
    }

    /// <summary>
    /// <b>The history has a history.</b>
    /// </summary>
    /// <remarks>
    /// The one thing about this data that could be wrong without looking wrong. Every date in
    /// anything written through the services comes from <c>IClock</c>, so a seed that did not travel
    /// would produce a firm where every client, every logged hour, every invoice and every incident
    /// happened in the same three seconds — the pages would fill up, and the ageing report would
    /// have one bucket, the burndown one point, and every trend a flat line.
    ///
    /// Asserted on the audit trail as well as on the rows, because the trail takes its timestamps
    /// from the same clock and is the half somebody would forget: a firm whose history is spread
    /// over a year and whose trail says it was all typed on Tuesday is a firm with no trail.
    /// </remarks>
    [Fact]
    public async Task The_seeded_firm_has_a_history_rather_than_an_afternoon()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var (_, database) = await SeedAsync(fixture);

        var hired = await database.Employees.AsNoTracking()
            .Select(one => one.StartsOn)
            .ToListAsync();

        Assert.True(
            hired.Max().DayNumber - hired.Min().DayNumber > 300,
            "Everybody was hired within a year of each other, so no screen about tenure, headcount "
            + "or review cycles has anything to show.");

        var trail = await database.AuditEntries.AsNoTracking()
            .Select(one => one.OccurredAt)
            .ToListAsync();

        Assert.True(
            (trail.Max() - trail.Min()) > TimeSpan.FromDays(300),
            "The whole audit trail is stamped within a year, so the history was written by a clock "
            + "that did not travel — see TravellingClock.");

        /*
         * And the most recent things are recent. A seed that wrote eighteen months ending a year
         * ago would satisfy everything above and show a dashboard where nothing has happened
         * lately, which is the same uselessness by a different route.
         */
        var lately = await database.Tickets.AsNoTracking().MaxAsync(one => one.RaisedAt);

        Assert.True(
            DateTimeOffset.UtcNow - lately < TimeSpan.FromDays(7),
            "Nothing has happened in the last week, so every screen about what is going on now is "
            + "empty. The timeline has to end at today, not at whenever it was written.");
    }

    /// <summary>
    /// The screens that report on the firm have something to report.
    /// </summary>
    /// <remarks>
    /// Marking and dating the data is not the same as it being worth looking at. This asserts the
    /// states the interesting screens exist for: an invoice that is late and one still a draft, a
    /// blocked piece of work and a finished one, an open incident beside resolved ones, a ticket
    /// already past its promise, an article overdue a check, a certificate close to expiring. Each
    /// is a screen that would otherwise render correctly and say nothing.
    /// </remarks>
    [Fact]
    public async Task The_seeded_firm_has_something_on_every_screen_worth_looking_at()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var (_, database) = await SeedAsync(fixture);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        Assert.True(
            await database.Invoices.AnyAsync(one => one.Status == JiranisokoTech.Domain.Money.InvoiceStatus.Draft),
            "no draft invoice, so the one state somebody is in the middle of is missing");

        Assert.True(
            await database.Invoices.AnyAsync(one =>
                one.Status == JiranisokoTech.Domain.Money.InvoiceStatus.Sent && one.DueOn < today),
            "nothing overdue, so the ageing report and the reminder ladder have nothing to show");

        Assert.True(
            await database.WorkItems.AnyAsync(one =>
                one.Status == JiranisokoTech.Domain.Work.WorkItemStatus.Blocked),
            "nothing blocked, so the column nobody looks at until it matters is empty");

        Assert.True(
            await database.WorkItems.AnyAsync(one => one.Status == JiranisokoTech.Domain.Work.WorkItemStatus.Done),
            "nothing finished, so a burndown has no shape");

        Assert.True(
            await database.Incidents.AnyAsync(one => one.ResolvedAt == null),
            "no open incident, so the count in the navigation is blank");

        Assert.True(
            await database.Incidents.AnyAsync(one => one.ResolvedAt != null),
            "no finished incident, so every duration on that page is missing");

        Assert.True(
            await database.Tickets.AnyAsync(one => one.AnswerOwedBy != null),
            "no ticket owed an answer, so the help desk's one count is blank");

        Assert.True(
            await database.Articles.AnyAsync(one => one.ReviewBy != null && one.ReviewBy < today),
            "no article overdue a check, which is the only number the knowledge base reports");

        Assert.True(
            await database.Resources.AnyAsync(one =>
                one.ExpiresOn != null && one.ExpiresOn < today.AddMonths(1)),
            "nothing expiring soon, so the register has no purpose and its job nothing to do");

        Assert.True(
            await database.TimeEntries.AnyAsync(one => one.ApprovedAt == null),
            "every hour is approved, so the dashboard's unapproved figure is always nought");
    }

    /// <summary>
    /// It refuses a database that has anybody's records in it.
    /// </summary>
    /// <remarks>
    /// The guard that matters, and it is checked here rather than trusted because the tool has no
    /// prompt — deliberately: a prompt in a tool nobody runs twice a year is one somebody answers
    /// without reading.
    ///
    /// Seeded twice, and the second run has to refuse. That is the realistic mistake: not somebody
    /// pointing it at the live database on purpose, but somebody running it again on a copy they
    /// had forgotten was already filled.
    /// </remarks>
    [Fact]
    public async Task It_refuses_a_database_that_already_has_records_in_it()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await SeedAsync(fixture);

        var refused = await Assert.ThrowsAnyAsync<Exception>(() => SeedAsync(fixture));

        /*
         * Any exception, because what is being asserted is that the second run does not quietly
         * double everything — the tool's own Program turns this into a printed sentence and exit 2,
         * and the sentence is what a person sees. Asserting the type here would be asserting which
         * service happened to notice first.
         */
        Assert.NotNull(refused);
    }

    /// <summary>
    /// Every marker the tool advertises is one it actually applies.
    /// </summary>
    /// <remarks>
    /// The tool prints a paragraph at the end saying what it marked. A marker named there and not
    /// applied would be worse than no marking at all, because somebody would have read the
    /// sentence. Cheap to assert and it costs nothing to keep.
    /// </remarks>
    [Fact]
    public void Nothing_in_the_marking_is_blank()
    {
        Assert.Contains("DEMONSTRATION", Marker.TradingName);
        Assert.Contains("DEMONSTRATION", Marker.LegalName);
        Assert.False(string.IsNullOrWhiteSpace(Marker.InvoicePrefix));
        Assert.EndsWith("-", Marker.Prefix);
        Assert.EndsWith(".invalid", Marker.EmailDomain);
        Assert.False(string.IsNullOrWhiteSpace(Marker.Actor));
        Assert.Equal(3, Marker.Currency.Length);

        Assert.StartsWith(Marker.Prefix, Marker.Code("anything"));
        Assert.EndsWith("@" + Marker.EmailDomain, Marker.Email("somebody"));
    }

    /// <summary>
    /// Build the container the tool builds, and fill the fixture's database with it.
    /// </summary>
    /// <remarks>
    /// The same registrations as <c>Program</c>, including the travelling clock, the named actor and
    /// the stubbed account access — because a test that built a different container would be
    /// testing a seed nobody runs.
    ///
    /// The connection string comes from the fixture rather than from configuration, which is the one
    /// difference: the fixture pins an in-memory SQLite database open for the length of the test,
    /// and handing its string to AddPersistence is how every other fixture here reaches it.
    /// </remarks>
    private static async Task<(DemoReport Report, TestDbContext Database)> SeedAsync(
        DatabaseFixture fixture)
    {
        var clock = new TravellingClock();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Git:Providers:GitHub:Secret"] = Marker.GitSecret,
                ["Git:Providers:GitLab:Secret"] = Marker.GitSecret,
                ["Git:Providers:Bitbucket:Secret"] = Marker.GitSecret,
                ["Git:Providers:AzureDevOps:Secret"] = Marker.GitSecret,
                ["Mail:Transport"] = "None",
            })
            .Build();

        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddMessaging(configuration);
        services.AddModules(configuration);

        var actor = new DemoUser();

        /*
         * The fixture's context rather than one AddPersistence would build, because the fixture
         * holds the in-memory database open and a second connection string would reach a different
         * one. Scoped, which is what every service resolved below expects.
         *
         * Handed the travelling clock and the named actor, not only the services above it. The
         * context is where the audit trail is captured and where its timestamps come from, so the
         * first version of this — which let the fixture's own clock and user through — produced a
         * seed whose trail was empty and stamped all at once. Both assertions failed, and neither
         * was the seed's fault.
         */
        services.AddScoped<AppDbContext>(_ => fixture.NewContext(clock, actor));
        services.AddSingleton<IClock>(clock);
        services.AddSingleton<ICurrentUser>(actor);
        services.RemoveAll<IAccountAccess>();
        services.AddSingleton<IAccountAccess>(new NoAccounts());

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var report = await new DemoFirm(
            scope.ServiceProvider, clock, TextWriter.Null).FillAsync();

        return (report, fixture.NewContext(clock, actor));
    }
}
