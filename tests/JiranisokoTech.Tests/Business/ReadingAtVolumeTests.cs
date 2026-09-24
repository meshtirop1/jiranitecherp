using JiranisokoTech.Application.Business;
using JiranisokoTech.Application.People;
using JiranisokoTech.Application.Settings;
using JiranisokoTech.Application.Work;
using JiranisokoTech.Domain.Clients;
using JiranisokoTech.Domain.Contracts;
using JiranisokoTech.Domain.Money;
using Money = JiranisokoTech.Domain.Common.Money;
using JiranisokoTech.Infrastructure.Business;
using JiranisokoTech.Infrastructure.Assets;
using JiranisokoTech.Infrastructure.People;
using JiranisokoTech.Infrastructure.Settings;
using JiranisokoTech.Infrastructure.Work;
using JiranisokoTech.Tests.Infrastructure;

namespace JiranisokoTech.Tests.Business;

/// <summary>
/// The reads that were changed after measuring them against a million rows.
/// </summary>
/// <remarks>
/// These hold the behaviour, not the speed. Nothing here can fail because something is
/// slow — a timing assertion on a build machine is a test that fails on a Tuesday when
/// somebody else is compiling — and every one of them would have passed before the change,
/// except for the two that say what the new arguments do.
///
/// What they are for is the next person: a take that is quietly dropped, or a count that
/// stops agreeing with the page it labels, turns a paged screen back into an unpaged one
/// without anything looking wrong. The scale check would find it again, and it needs a
/// database and several minutes; these run in the suite.
///
/// See docs/performance.md for what the measuring found.
/// </remarks>
public class ReadingAtVolumeTests
{
    private sealed class Module(DatabaseFixture db) : IAsyncDisposable
    {
        private readonly TestDbContext _context = db.NewContext();

        private BusinessRepository Repository => new(_context);

        private SettingsService Settings => new(new SettingsRepository(_context));

        public ClientService Clients => new(Repository);

        public ContractService Contracts => new(Repository, Settings, db.Clock);

        public InvoiceService Invoices => new(Repository, Settings, db.Clock);

        public PeopleService People => new(new PeopleRepository(_context), new AssetRepository(_context), db.Clock);

        public TimesheetService Timesheets =>
            new(Repository, new PeopleRepository(_context), db.Clock);

        public BusinessQueries Reads => new(_context, db.Clock);

        public WorkQueries Work => new(_context);

        public async ValueTask DisposeAsync() => await _context.DisposeAsync();
    }

    /// <summary>
    /// A page of invoices is a page, and the count is of all of them.
    /// </summary>
    /// <remarks>
    /// The two halves have to be true together. A take that is ignored gives an unpaged
    /// screen that looks paged; a count that counts the page gives a screen saying "1–5 of
    /// 5" over a list of fifty — and neither gets reported as a bug, people just stop
    /// believing the number.
    /// </remarks>
    [Fact]
    public async Task A_page_of_invoices_is_a_page_and_the_count_is_of_all_of_them()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Haulage");

        await Billable(module, client.Id, "C-DEPOT");

        for (var made = 0; made < 7; made++)
        {
            await module.Invoices.DraftAsync(client.Id);
        }

        Assert.Equal(7, await module.Reads.CountInvoicesAsync());

        var page = await module.Reads.InvoicesAsync(take: 3);

        Assert.Equal(3, page.Count);

        var second = await module.Reads.InvoicesAsync(skip: 3, take: 3);

        Assert.Equal(3, second.Count);
        Assert.Empty(page.Select(one => one.Id).Intersect(second.Select(one => one.Id)));

        // The last page is short rather than absent, and the numbers still add up.
        Assert.Single(await module.Reads.InvoicesAsync(skip: 6, take: 3));
    }

    /// <summary>
    /// The count agrees with the list when both are filtered the same way.
    /// </summary>
    /// <remarks>
    /// They are two expressions over two queries, and the filtering is shared between them
    /// for exactly this reason — a condition added to one and forgotten in the other is a
    /// screen whose total is of a different list than the one under it.
    /// </remarks>
    [Fact]
    public async Task The_count_and_the_list_are_filtered_the_same_way()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Haulage");
        var other = await module.Clients.TakeOnAsync("Bluebird Logistics", "bluebird");

        await Billable(module, client.Id, "C-ACME");
        await Billable(module, other.Id, "C-BLUE");

        foreach (var whose in new[] { client.Id, client.Id, other.Id })
        {
            await module.Invoices.DraftAsync(whose);
        }

        Assert.Equal(2, await module.Reads.CountInvoicesAsync(clientId: client.Id));
        Assert.Equal(2, (await module.Reads.InvoicesAsync(clientId: client.Id)).Count);

        Assert.Equal(
            await module.Reads.CountInvoicesAsync(status: InvoiceStatus.Draft),
            (await module.Reads.InvoicesAsync(status: InvoiceStatus.Draft)).Count);
    }

    /// <summary>
    /// Give a client a live contract, which is what says they may be billed at all.
    /// </summary>
    private static async Task Billable(Module module, Guid clientId, string reference)
    {
        var contract = await module.Contracts.DraftAsync(clientId, reference, "Depot work");

        await module.Contracts.AgreeAsync(
            contract.Id,
            "Depot work, year one",
            Money.Of(1_200_000_00, "KES"),
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 12, 31));

        await module.Contracts.ActivateAsync(contract.Id);
    }

    /// <summary>
    /// The approval queue reads oldest first.
    /// </summary>
    /// <remarks>
    /// Not a preference. A timesheet is read newest first, to check what was just logged,
    /// and this query serves both — but the queue is capped, and with a cap the order
    /// decides which entries are hidden. Newest-first would have hidden the oldest: exactly
    /// the ones somebody is waiting on.
    /// </remarks>
    [Fact]
    public async Task The_approval_queue_reads_oldest_first_and_a_timesheet_reads_newest_first()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var engineer = await module.People.HireAsync("Grace Wanjiru", new DateOnly(2026, 1, 5));

        // Three days already worked. The clock says the 21st, and hours cannot be logged
        // for a day that has not happened.
        var monday = new DateOnly(2026, 9, 14);

        foreach (var day in new[] { monday, monday.AddDays(1), monday.AddDays(2) })
        {
            await module.Timesheets.LogAsync(engineer.Id, day, 60, note: $"Work on {day:d MMM}");
        }

        var queue = await module.Reads.TimeAsync(awaitingApprovalOnly: true);

        Assert.Equal(monday, queue[0].On);
        Assert.Equal(monday.AddDays(2), queue[^1].On);

        var timesheet = await module.Reads.TimeAsync(employeeId: engineer.Id);

        Assert.Equal(monday.AddDays(2), timesheet[0].On);
    }

    /// <summary>
    /// A capped queue hides the newest, never the oldest.
    /// </summary>
    /// <remarks>
    /// The point of the ordering above, stated as the thing that would actually go wrong.
    /// </remarks>
    [Fact]
    public async Task A_capped_queue_keeps_the_oldest()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var engineer = await module.People.HireAsync("Grace Wanjiru", new DateOnly(2026, 1, 5));

        // Three days already worked. The clock says the 21st, and hours cannot be logged
        // for a day that has not happened.
        var monday = new DateOnly(2026, 9, 14);

        foreach (var day in new[] { monday, monday.AddDays(1), monday.AddDays(2) })
        {
            await module.Timesheets.LogAsync(engineer.Id, day, 60, note: $"Work on {day:d MMM}");
        }

        var capped = await module.Reads.TimeAsync(awaitingApprovalOnly: true, take: 2);

        Assert.Equal([monday, monday.AddDays(1)], capped.Select(entry => entry.On));
    }

    /// <summary>
    /// One work item is read by its identifier.
    /// </summary>
    /// <remarks>
    /// The screen that shows one used to read every work item in the system and pick its
    /// own out of the list with FirstOrDefault. Invisible with forty rows; three quarters
    /// of a second and eighty thousand objects per view at a hundred and twenty thousand.
    /// </remarks>
    [Fact]
    public async Task One_work_item_is_read_by_its_identifier()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var engineer = await module.People.HireAsync("Grace Wanjiru", new DateOnly(2026, 1, 5));

        var work = new WorkService(
            new WorkRepository(db.NewContext()), new PeopleRepository(db.NewContext()), db.Clock);

        var first = await work.RaiseAsync("Fit the tracker", engineer.Id);
        await work.RaiseAsync("Wire the depot", engineer.Id);

        var found = await module.Work.ItemAsync(first.Id);

        Assert.NotNull(found);
        Assert.Equal("Fit the tracker", found.Title);

        Assert.Null(await module.Work.ItemAsync(Guid.CreateVersion7()));
    }

    /// <summary>A board asks for as many as it will show, and no more.</summary>
    [Fact]
    public async Task A_board_takes_only_what_it_will_show()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var engineer = await module.People.HireAsync("Grace Wanjiru", new DateOnly(2026, 1, 5));

        var work = new WorkService(
            new WorkRepository(db.NewContext()), new PeopleRepository(db.NewContext()), db.Clock);

        for (var raised = 0; raised < 5; raised++)
        {
            await work.RaiseAsync($"Item {raised}", engineer.Id);
        }

        Assert.Equal(5, await module.Work.CountItemsAsync(openOnly: true));
        Assert.Equal(2, (await module.Work.ItemsAsync(openOnly: true, take: 2)).Count);
    }
}
