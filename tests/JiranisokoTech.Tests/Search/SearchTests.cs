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
using JiranisokoTech.Infrastructure.Authorization;

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

        public WorkQueries WorkQueries => new(_context);

        public WorkService Work =>
            new(new WorkRepository(_context), new PeopleRepository(_context), db.Clock);

        public ClientService Clients => new(new BusinessRepository(_context));

        public InvoiceService Invoices => new(
            new BusinessRepository(_context),
            new SettingsService(new SettingsRepository(_context)),
            db.Clock);

        public SearchQueries Search => new(_context, new Reaches(_context));

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

        /*
         * Put on the project, because a developer's project permission is now scoped to
         * the ones they are on. This assertion used to pass because it was not scoped at
         * all — which meant every engineer could find every project in the firm by name.
         */
        var project = (await module.WorkQueries.ProjectsAsync())
            .First(row => row.Name.Contains("Acme", StringComparison.OrdinalIgnoreCase));

        var person = await module.People.HireAsync("Meshack Tirop", db.Clock.Today);
        await module.People.StartAsync(person.Id);

        var item = await module.Work.RaiseAsync("Fit the tracker", person.Id, project.Id);
        await module.Work.AssignAsync(item.Id, person.Id);

        var developer = HeldBy(Roles.Developer);
        var found = await module.Search.FindAsync("acme", developer, person.Id);

        Assert.Contains(found, result => result.Kind == ResultKind.Project);
        Assert.DoesNotContain(found, result => result.Kind == ResultKind.Client);
        Assert.DoesNotContain(found, result => result.Kind == ResultKind.Person);

        // And the invoice is not found by its own number either.
        var byNumber = await module.Search.FindAsync(invoice.Number, developer, person.Id);

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
    /// The narrower project permission finds the projects somebody is on, and no others.
    /// </summary>
    /// <remarks>
    /// This test previously asserted the opposite, and was right to fail when the
    /// behaviour was corrected — so what it asserted is worth recording.
    ///
    /// It held that projects.view_member was enough to search every project, reasoning
    /// that checking only projects.view_all would leave the search box finding nothing
    /// for the people who do the work. That reasoning was sound and the conclusion was
    /// still wrong: the answer was never "everything or nothing", it was the projects they
    /// are on. There was simply no way to express that until Reach existed, so the wide
    /// reading was chosen and a test was written to lock it in.
    ///
    /// The effect was that every engineer in the firm could find every project by name
    /// through the search box, while holding a permission that says otherwise.
    /// </remarks>
    [Fact]
    public async Task The_narrower_project_permission_finds_only_the_projects_somebody_is_on()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        await PopulateAsync(module);

        // Not on it, and holding only the narrow permission.
        var stranger = await module.Search.FindAsync(
            "acme",
            new HashSet<string> { Permissions.ProjectsViewMember },
            Guid.CreateVersion7());

        Assert.DoesNotContain(stranger, result => result.Kind == ResultKind.Project);

        // And nothing at all for an account with no staff record behind it, because
        // there is no honest set of projects to give one.
        var unlinked = await module.Search.FindAsync(
            "acme", new HashSet<string> { Permissions.ProjectsViewMember });

        Assert.DoesNotContain(unlinked, result => result.Kind == ResultKind.Project);
    }

    /// <summary>
    /// Somebody assigned work on a project is on that project.
    /// </summary>
    /// <remarks>
    /// The definition of membership, asserted because it is the half that makes the
    /// narrowing usable rather than merely safe. Without it, tightening the search would
    /// have produced exactly the empty box the old test was written to prevent.
    /// </remarks>
    [Fact]
    public async Task Somebody_assigned_work_on_a_project_can_search_it()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        await PopulateAsync(module);

        var project = (await module.WorkQueries.ProjectsAsync())
            .First(row => row.Name.Contains("Acme", StringComparison.OrdinalIgnoreCase));

        var person = await module.People.HireAsync("Meshack Tirop", db.Clock.Today);
        await module.People.StartAsync(person.Id);

        var item = await module.Work.RaiseAsync("Fit the tracker", person.Id, project.Id);
        await module.Work.AssignAsync(item.Id, person.Id);

        var found = await module.Search.FindAsync(
            "acme",
            new HashSet<string> { Permissions.ProjectsViewMember },
            person.Id);

        Assert.Contains(found, result => result.Kind == ResultKind.Project);
    }

    private static HashSet<string> HeldBy(string role) =>
        [.. Roles.PermissionsFor(role)];

    /// <summary>
    /// Somebody who may only read their own work does not find anybody else's.
    /// </summary>
    /// <remarks>
    /// A leak that was live. The work-item group ran for anybody holding tasks.view_own, which
    /// is every engineer in the firm, and matched every title there is — while Item.razor
    /// decides whether a person may read one as tasks.view_all OR being the assignee. So the
    /// box handed out links to pages that then refused them, and, worse, handed out the titles
    /// on the way: a work item called "Investigate Achieng's expense claims" discloses the
    /// thing it is about before anybody clicks.
    ///
    /// This file's own header says search is the one place where getting a permission wrong
    /// shows up as somebody being shown something rather than refused it, and that nobody
    /// reports being shown too much. That is exactly what happened.
    /// </remarks>
    [Fact]
    public async Task Somebody_who_may_read_only_their_own_work_does_not_find_anybody_elses()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var mine = await module.People.HireAsync("Grace Wanjiru", Monday);
        var theirs = await module.People.HireAsync("Otieno Barasa", Monday);

        // Hired is not started, and work cannot be given to somebody who has not started.
        await module.People.StartAsync(mine.Id);
        await module.People.StartAsync(theirs.Id);

        var raised = await module.Work.RaiseAsync("Confidential payroll rebuild", mine.Id);
        await module.Work.AssignAsync(raised.Id, mine.Id);

        var other = await module.Work.RaiseAsync("Confidential board pack", theirs.Id);
        await module.Work.AssignAsync(other.Id, theirs.Id);

        var found = await module.Search.FindAsync(
            "confidential", OnlyOwnWork, employeeId: mine.Id);

        Assert.Contains(found, result => result.Title.Contains("payroll rebuild"));
        Assert.DoesNotContain(found, result => result.Title.Contains("board pack"));
    }

    /// <summary>
    /// A department head does not find people the roster hides from them.
    /// </summary>
    /// <remarks>
    /// The second live leak, and the more surprising one, because the fix already existed and
    /// this file had not learned it. Reaches.DepartmentsAsync narrows employees.view to the
    /// departments somebody actually heads, and Roster.razor uses it for exactly that — so a
    /// head's own staff list is narrowed while the search box above it was not. The same
    /// person, the same screen, two different answers about who works here.
    /// </remarks>
    [Fact]
    public async Task A_head_does_not_find_people_their_own_roster_hides()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var head = await module.People.HireAsync("Njeri Kamau", Monday);
        await module.People.HireAsync("Mutiso Kilonzo", Monday);

        var delivery = await module.People.OpenDepartmentAsync("Delivery");
        await module.People.AppointHeadAsync(delivery.Id, head.Id);

        /*
         * The head is in the department they run; the other person is in none. A head whose
         * reach is their own department should find themselves and not the stranger.
         */
        await module.People.MoveAsync(head.Id, delivery.Id);

        /*
         * A term long enough to be searched at all. The first version of this test asked for
         * "k", which is under ShortestTerm, so FindAsync returned an empty list and the
         * assertion below passed without exercising anything — a test that cannot fail, which
         * is worse than no test.
         */
        var found = await module.Search.FindAsync(
            "kilonzo", HeadOfADepartment, employeeId: head.Id);

        Assert.DoesNotContain(
            found,
            result => result.Kind == ResultKind.Person
                && result.Title.Contains("Mutiso"));
    }

    /// <summary>What an engineer holds: their own work, and nothing wider.</summary>
    private static readonly HashSet<string> OnlyOwnWork = [Permissions.TasksViewOwn];

    /// <summary>What a department head holds about people: the narrowable one.</summary>
    private static readonly HashSet<string> HeadOfADepartment = [Permissions.EmployeesView];
}
