using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Application.Business;
using JiranisokoTech.Application.People;
using JiranisokoTech.Application.Settings;
using JiranisokoTech.Application.Work;
using JiranisokoTech.Infrastructure.Business;
using JiranisokoTech.Infrastructure.People;
using JiranisokoTech.Infrastructure.Search;
using JiranisokoTech.Infrastructure.Settings;
using JiranisokoTech.Infrastructure.Work;
using JiranisokoTech.Tests.Infrastructure;
using Money = JiranisokoTech.Domain.Common.Money;

namespace JiranisokoTech.Tests.Search;

/// <summary>
/// One box that looks in six tables, and the reason that is worth testing.
/// </summary>
/// <remarks>
/// Every other page in this system is behind a single permission, so getting
/// one wrong shows up as somebody being refused a page. Search is the one place
/// where getting it wrong shows up as somebody being shown something instead —
/// and nobody reports being shown too much.
/// </remarks>
public class SearchTests
{
    private static readonly DateOnly Monday = new(2026, 9, 21);

    private sealed class Module(DatabaseFixture db) : IAsyncDisposable
    {
        private readonly TestDbContext _context = db.NewContext();

        public PeopleService People => new(new PeopleRepository(_context), db.Clock);

        public WorkService Work =>
            new(new WorkRepository(_context), new PeopleRepository(_context), db.Clock);

        public ClientService Clients => new(new BusinessRepository(_context));

        public InvoiceService Invoices => new(
            new BusinessRepository(_context),
            new SettingsService(new SettingsRepository(_context)),
            db.Clock);

        public SearchQueries Search => new(_context);

        public ValueTask DisposeAsync() => _context.DisposeAsync();
    }

    /// <summary>Everything a manager holds, for the tests about matching.</summary>
    private static readonly HashSet<string> Everything = [.. Permissions.All];

    private static async Task PopulateAsync(Module module)
    {
        await module.People.HireAsync("Duncan Kiptoo", Monday);
        await module.Clients.TakeOnAsync("Acme Logistics");
        await module.Work.BeginProjectAsync("Acme fleet tracking");
    }

    [Fact]
    public async Task A_term_matches_across_several_kinds_at_once()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        await PopulateAsync(module);

        var found = await module.Search.FindAsync("acme", Everything);

        Assert.Contains(found, result => result.Kind == ResultKind.Client);
        Assert.Contains(found, result => result.Kind == ResultKind.Project);
        Assert.DoesNotContain(found, result => result.Kind == ResultKind.Person);
    }

    /// <summary>
    /// Case does not matter, and this is the test that would have caught the
    /// providers disagreeing.
    /// </summary>
    /// <remarks>
    /// SQLite's LIKE ignores case for ASCII and PostgreSQL's does not. A query
    /// written the obvious way passes here and then finds nothing in production
    /// for anybody who types their search in lower case — which is everybody.
    /// The query lowers both sides for that reason; this asserts it.
    /// </remarks>
    [Theory]
    [InlineData("duncan")]
    [InlineData("DUNCAN")]
    [InlineData("DuNcAn")]
    [InlineData(" duncan ")]
    public async Task Case_and_surrounding_space_do_not_matter(string term)
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        await PopulateAsync(module);

        var found = await module.Search.FindAsync(term, Everything);

        Assert.Contains(found, result => result.Title == "Duncan Kiptoo");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a")]
    public async Task A_term_too_short_to_mean_anything_finds_nothing(string term)
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        await PopulateAsync(module);

        Assert.Empty(await module.Search.FindAsync(term, Everything));
    }

    /// <summary>
    /// A developer searching does not find clients, invoices or candidates.
    /// </summary>
    /// <remarks>
    /// The point of the whole file. A developer holds the work and project
    /// permissions and none of the commercial ones, so a search that matched
    /// everything would hand them the client list, what each is worth, and who
    /// has applied for a job — none of which any page would show them.
    /// </remarks>
    [Fact]
    public async Task A_developer_finds_their_work_and_nothing_commercial()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        await PopulateAsync(module);

        var client = (await module.Search.FindAsync("acme", Everything))
            .First(result => result.Kind == ResultKind.Client);

        var invoice = await module.Invoices.DraftAsync(client.Id);

        var developer = HeldBy(Roles.Developer);
        var found = await module.Search.FindAsync("acme", developer);

        Assert.Contains(found, result => result.Kind == ResultKind.Project);
        Assert.DoesNotContain(found, result => result.Kind == ResultKind.Client);
        Assert.DoesNotContain(found, result => result.Kind == ResultKind.Person);

        // And the invoice is not found by its own number either.
        var byNumber = await module.Search.FindAsync(invoice.Number, developer);

        Assert.DoesNotContain(byNumber, result => result.Kind == ResultKind.Invoice);
    }

    /// <summary>
    /// Holding nothing finds nothing, whatever is in the database.
    /// </summary>
    /// <remarks>
    /// An account with no role yet — invited and not assigned one — is a real
    /// state, and it is the state in which a leak would be widest.
    /// </remarks>
    [Fact]
    public async Task Somebody_holding_no_permissions_finds_nothing()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        await PopulateAsync(module);

        Assert.Empty(await module.Search.FindAsync("acme", new HashSet<string>()));
        Assert.Empty(await module.Search.FindAsync("duncan", new HashSet<string>()));
    }

    /// <summary>
    /// An invoice is found by the number a client quotes back.
    /// </summary>
    [Fact]
    public async Task An_invoice_is_found_by_its_number()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Logistics");
        var invoice = await module.Invoices.DraftAsync(client.Id);
        await module.Invoices.AddLineAsync(invoice.Id, "Work", 1, Money.Of(1_000_00, "KES"));

        var found = await module.Search.FindAsync(invoice.Number, Everything);

        var hit = Assert.Single(found, result => result.Kind == ResultKind.Invoice);

        Assert.Equal(invoice.Number, hit.Title);
        Assert.Equal($"/invoices/{invoice.Id}", hit.Href);
    }

    /// <summary>
    /// An engineer who may only see the projects they are on can still search
    /// projects.
    /// </summary>
    /// <remarks>
    /// The same mistake as the work board, which shipped unopenable because no
    /// role held tasks.view_own. Checking only projects.view_all here would have
    /// meant a search box that found no projects for the people who work on
    /// them.
    /// </remarks>
    [Fact]
    public async Task The_narrower_project_permission_is_enough_to_search_projects()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        await PopulateAsync(module);

        var found = await module.Search.FindAsync(
            "acme", new HashSet<string> { Permissions.ProjectsViewMember });

        Assert.Contains(found, result => result.Kind == ResultKind.Project);
    }

    private static HashSet<string> HeldBy(string role) =>
        [.. Roles.PermissionsFor(role)];
}
