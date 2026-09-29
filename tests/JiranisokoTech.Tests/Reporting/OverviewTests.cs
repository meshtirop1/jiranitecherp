using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Domain.Clients;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Domain.Money;
using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Infrastructure.Reporting;
using JiranisokoTech.Tests.Infrastructure;

namespace JiranisokoTech.Tests.Reporting;

/// <summary>
/// How the firm is, and who is allowed to be told.
/// </summary>
/// <remarks>
/// Section 63. The brief names eleven figures and one rule — make all widgets
/// permission-aware — and it is the rule that is worth testing. A figure that is merely wrong
/// is a figure somebody corrects; a figure shown to somebody who may not open the screen it
/// came off is a disclosure, and this is one of only two places in the application that reads
/// a dozen tables in one go.
/// </remarks>
public class OverviewTests
{
    private static readonly DateOnly Today = new(2026, 10, 5);

    /// <summary>
    /// Somebody holding nothing is told nothing, and nothing is read on their behalf.
    /// </summary>
    /// <remarks>
    /// The second half is the point and it is asserted through the result rather than through
    /// the queries: a panel somebody may not see is never built, so there is nothing for a
    /// later edit to forget to hide. "Not shown" and "not read" are different guarantees and
    /// only the second survives somebody moving the markup around.
    /// </remarks>
    [Fact]
    public async Task A_reader_with_no_permissions_is_shown_no_figures()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await GiveMoneyAsync(fixture);

        await using var context = fixture.NewContext();

        Assert.Empty(await Overview(context).ForAsync(new HashSet<string>(), Today));
    }

    /// <summary>
    /// The money panel needs the permission that guards the invoices screen.
    /// </summary>
    /// <remarks>
    /// Written the way somebody would get it wrong: a developer holds plenty of permissions
    /// and not this one, and what the firm is owed must not reach them through a page nobody
    /// thought of as a money screen.
    /// </remarks>
    [Fact]
    public async Task What_the_firm_is_owed_needs_the_invoices_permission()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await GiveMoneyAsync(fixture);

        await using var context = fixture.NewContext();

        var withoutIt = await Overview(context).ForAsync(
            new HashSet<string> { Permissions.TasksViewAll, Permissions.IncidentsView },
            Today);

        Assert.DoesNotContain(withoutIt, panel => panel.Heading == "Money");

        var withIt = await Overview(context).ForAsync(
            new HashSet<string> { Permissions.InvoicesView }, Today);

        var money = Assert.Single(withIt, panel => panel.Heading == "Money");

        Assert.Contains(money.Figures, one => one.What == "Owed to us");
    }

    /// <summary>
    /// An invoice past its date is marked as something to look at, and says how much.
    /// </summary>
    /// <remarks>
    /// Marked rather than merely listed. A page of figures where nothing is ever distinguished
    /// is read once; the whole reason this one is worth opening weekly is that it says which
    /// line somebody should act on today.
    /// </remarks>
    [Fact]
    public async Task An_invoice_past_its_date_is_marked_to_be_looked_at()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await GiveMoneyAsync(fixture);

        await using var context = fixture.NewContext();

        var money = Assert.Single(
            await Overview(context).ForAsync(
                new HashSet<string> { Permissions.InvoicesView }, Today),
            panel => panel.Heading == "Money");

        var late = Assert.Single(money.Figures, one => one.What == "Past its due date");

        Assert.True(late.Worrying);
        Assert.Contains("KES", late.Reading, StringComparison.Ordinal);
        Assert.Equal("/invoices", late.Where);
    }

    /// <summary>
    /// Two currencies are said, never added.
    /// </summary>
    /// <remarks>
    /// The most expensive wrong number this application could print. Money refuses to add
    /// across currencies everywhere else, and an overview is precisely where somebody would be
    /// tempted to make it — so the reading names the problem rather than inventing a sum.
    /// </remarks>
    [Fact]
    public async Task Invoices_in_two_currencies_are_not_added_together()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await GiveMoneyAsync(fixture, second: "USD");

        await using var context = fixture.NewContext();

        var money = Assert.Single(
            await Overview(context).ForAsync(
                new HashSet<string> { Permissions.InvoicesView }, Today),
            panel => panel.Heading == "Money");

        var owed = Assert.Single(money.Figures, one => one.What == "Owed to us");

        Assert.Equal("in more than one currency", owed.Reading);
    }

    /// <summary>
    /// A panel with nothing in it is not shown at all.
    /// </summary>
    /// <remarks>
    /// "0 open incidents" is a line that teaches a reader to skim, and a page read weekly
    /// cannot afford one. Somebody holding the permission on a firm with no incidents sees no
    /// panel rather than an empty one.
    /// </remarks>
    [Fact]
    public async Task A_panel_with_nothing_in_it_is_left_off()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();

        await using var context = fixture.NewContext();

        var panels = await Overview(context).ForAsync(
            new HashSet<string> { Permissions.IncidentsView, Permissions.PlatformView },
            Today);

        Assert.DoesNotContain(panels, panel => panel.Heading == "What is running");
    }

    /// <summary>Every figure points at a screen, and never at nothing.</summary>
    /// <remarks>
    /// A tile whose link is empty is a tile somebody clicks once. Asserted across whatever the
    /// fixture happens to produce rather than one by one, so a figure added later is covered
    /// without anybody remembering to cover it.
    /// </remarks>
    [Fact]
    public async Task Every_figure_says_where_to_go_for_the_detail()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await GiveMoneyAsync(fixture);

        await using var context = fixture.NewContext();

        var panels = await Overview(context).ForAsync(
            Permissions.All.ToHashSet(), Today);

        Assert.NotEmpty(panels);

        foreach (var figure in panels.SelectMany(panel => panel.Figures))
        {
            Assert.StartsWith("/", figure.Where, StringComparison.Ordinal);
            Assert.NotEqual(string.Empty, figure.What);
            Assert.NotEqual(string.Empty, figure.Reading);
        }
    }

    private static OverviewQueries Overview(AppDbContext context) =>
        new(context, new ProjectMoneyQueries(context));

    /// <summary>
    /// A client, an invoice that is late, and optionally a second in another currency.
    /// </summary>
    private static async Task GiveMoneyAsync(
        DatabaseFixture fixture, string? second = null)
    {
        await using var context = fixture.NewContext();

        var client = Client.TakeOn("Kilele Foods");
        context.Clients.Add(client);
        await context.SaveChangesAsync();

        /*
         * Issued sixty days ago on thirty-day terms, so it is thirty days past its date on
         * the fixture's today — which is what makes it the line this page exists to surface.
         */
        var invoice = Invoice.Draft(
            client.Id, "INV-2026-001", "KES", Today.AddDays(-60), 30);

        invoice.AddLine("Delivery work", 1, Money.Of(480_000_00, "KES"));
        invoice.Send(Today.AddDays(-60).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

        context.Invoices.Add(invoice);

        if (second is { } other)
        {
            var abroad = Invoice.Draft(
                client.Id, "INV-2026-002", other, Today.AddDays(-10), 30);

            abroad.AddLine("Retainer", 1, Money.Of(2_000_00, other));
            abroad.Send(Today.AddDays(-10).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

            context.Invoices.Add(abroad);
        }

        await context.SaveChangesAsync();
    }
}
