using JiranisokoTech.Application.Business;
using JiranisokoTech.Application.People;
using JiranisokoTech.Application.Settings;
using JiranisokoTech.Application.Work;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Domain.Money;
using JiranisokoTech.Infrastructure.Reporting;
using JiranisokoTech.Tests.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace JiranisokoTech.Tests.Reporting;

/// <summary>
/// What a project earned, what it took, and what is left, as /projects/money reports it.
/// </summary>
/// <remarks>
/// <b>These exist because nothing tested it.</b> The calculation makes five decisions, each
/// written down in the query — only invoices actually sent count as earned, only approved hours
/// as labour, only expenses actually paid out, hours are costed at the firm's standard rate,
/// and money in two currencies produces no margin at all rather than a wrong one — and not one
/// of them had a test. An audit of the implementation against the brief found that on the
/// screen that answers the brief's finance workflow.
///
/// Each figure is worked out by hand in the test rather than by calling anything the query
/// uses, so that a test cannot pass by agreeing with the code it is checking. Each test has
/// its own project, because the class shares one database.
/// </remarks>
public class ProjectMoneyTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    /// <summary>KES 2,000.00 an hour.</summary>
    private const long CostPerHour = 2_000_00;

    /// <summary>
    /// A worked example: one sent invoice, approved hours and a paid expense, with a draft
    /// invoice, unapproved hours and an unpaid claim beside them that must not count.
    /// </summary>
    /// <remarks>
    /// Earned KES 100,000 (the sent invoice; the draft for 40,000 is left out). Labour is 10
    /// approved hours at 2,000 — 20,000; the 3 unapproved hours are left out. Expenses are the
    /// 5,000 claim that was paid; the 7,000 one that was only approved is left out. So the
    /// cost is 25,000, the margin 75,000, three quarters of what was invoiced, and half of the
    /// 50,000 budget has gone.
    /// </remarks>
    [Fact]
    public async Task A_projects_margin_counts_only_what_was_sent_approved_and_paid()
    {
        var project = await AProjectAsync(budget: 50_000_00);

        await factory.InScopeAsync(async services =>
        {
            var person = await SomebodyAsync(services);
            var manager = await SomebodyAsync(services);
            var invoices = services.GetRequiredService<InvoiceService>();
            var time = services.GetRequiredService<TimesheetService>();
            var claims = services.GetRequiredService<ExpenseService>();
            var client = await AClientAsync(services);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);

            var sent = await invoices.DraftAsync(client);
            await invoices.AddLineAsync(sent.Id, "Statement module", 1, Money.Of(100_000_00, "KES"));
            await invoices.BillForAsync(sent.Id, project);
            await invoices.SendAsync(sent.Id);

            var draft = await invoices.DraftAsync(client);
            await invoices.AddLineAsync(draft.Id, "Not sent yet", 1, Money.Of(40_000_00, "KES"));
            await invoices.BillForAsync(draft.Id, project);

            var approved = await time.LogAsync(person, today.AddDays(-2), 600, projectId: project);
            await time.ApproveAsync(approved.Id, manager);
            await time.LogAsync(person, today.AddDays(-1), 180, projectId: project);

            var paid = await claims.ClaimAsync(
                person, Money.Of(5_000_00, "KES"), ExpenseCategory.Travel, today, "Site visit", project);
            await claims.SubmitAsync(paid.Id);
            await claims.RecordDecisionAsync(paid.Id, approved: true, reason: null);
            await claims.PayAsync(paid.Id, "M-PESA QJ12");

            var owed = await claims.ClaimAsync(
                person, Money.Of(7_000_00, "KES"), ExpenseCategory.Travel, today, "Second visit", project);
            await claims.SubmitAsync(owed.Id);
            await claims.RecordDecisionAsync(owed.Id, approved: true, reason: null);

            var row = (await services.GetRequiredService<ProjectMoneyQueries>().AllAsync())
                .Single(one => one.Id == project);

            Assert.Equal(Money.Of(100_000_00, "KES"), row.Invoiced);
            Assert.Equal(600, row.ApprovedMinutes);
            Assert.Equal(Money.Of(20_000_00, "KES"), row.Labour);
            Assert.Equal(Money.Of(5_000_00, "KES"), row.Expenses);
            Assert.Equal(Money.Of(25_000_00, "KES"), row.Cost);
            Assert.Equal(Money.Of(75_000_00, "KES"), row.Margin);
            Assert.Equal(75, row.MarginShare);
            Assert.Equal(50, row.BudgetUsedShare);
            Assert.False(row.IsOverBudget);
        });
    }

    /// <summary>
    /// Money in two currencies produces no margin, and says why, rather than a wrong one.
    /// </summary>
    [Fact]
    public async Task A_project_billed_in_two_currencies_has_no_margin_and_says_so()
    {
        var project = await AProjectAsync(budget: null);

        await factory.InScopeAsync(async services =>
        {
            var invoices = services.GetRequiredService<InvoiceService>();
            var client = await AClientAsync(services);

            var shillings = await invoices.DraftAsync(client);
            await invoices.AddLineAsync(shillings.Id, "Build", 1, Money.Of(100_000_00, "KES"));
            await invoices.BillForAsync(shillings.Id, project);
            await invoices.SendAsync(shillings.Id);

            var dollars = await invoices.DraftAsync(client, "USD");
            await invoices.AddLineAsync(dollars.Id, "Hosting", 1, Money.Of(1_200_00, "USD"));
            await invoices.BillForAsync(dollars.Id, project);
            await invoices.SendAsync(dollars.Id);

            var row = (await services.GetRequiredService<ProjectMoneyQueries>().AllAsync())
                .Single(one => one.Id == project);

            Assert.True(row.HasMixedCurrencies);
            Assert.Null(row.Invoiced);
            Assert.Null(row.Margin);
        });
    }

    /// <summary>
    /// A project that has cost more than its budget says so.
    /// </summary>
    /// <remarks>30 approved hours at 2,000 is 60,000 against a budget of 50,000 — 120 per cent.</remarks>
    [Fact]
    public async Task A_project_that_has_cost_more_than_its_budget_is_over_it()
    {
        var project = await AProjectAsync(budget: 50_000_00);

        await factory.InScopeAsync(async services =>
        {
            var person = await SomebodyAsync(services);
            var manager = await SomebodyAsync(services);
            var time = services.GetRequiredService<TimesheetService>();
            var today = DateOnly.FromDateTime(DateTime.UtcNow);

            // Three ten-hour days, because a day is refused past sixteen hours.
            for (var day = 1; day <= 3; day++)
            {
                var entry = await time.LogAsync(person, today.AddDays(-day), 600, projectId: project);
                await time.ApproveAsync(entry.Id, manager);
            }

            var row = (await services.GetRequiredService<ProjectMoneyQueries>().AllAsync())
                .Single(one => one.Id == project);

            Assert.Equal(Money.Of(60_000_00, "KES"), row.Cost);
            Assert.True(row.IsOverBudget);
            Assert.Equal(120, row.BudgetUsedShare);
        });
    }

    private async Task<Guid> AProjectAsync(long? budget)
    {
        Guid id = default;

        await factory.InScopeAsync(async services =>
        {
            await services.GetRequiredService<SettingsService>().CostAnHourAtAsync(CostPerHour);

            var work = services.GetRequiredService<WorkService>();
            var project = await work.BeginProjectAsync(
                "Statements " + Guid.CreateVersion7().ToString("N")[^8..]);

            if (budget is { } amount)
            {
                await work.BudgetAsync(project.Id, amount, "KES");
            }

            id = project.Id;
        });

        return id;
    }

    private static async Task<Guid> SomebodyAsync(IServiceProvider services)
    {
        var people = services.GetRequiredService<PeopleService>();

        var person = await people.HireAsync(
            "Developer " + Guid.CreateVersion7().ToString("N")[^8..],
            DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-60));

        await people.StartAsync(person.Id);

        return person.Id;
    }

    private static async Task<Guid> AClientAsync(IServiceProvider services)
    {
        var clients = services.GetRequiredService<ClientService>();
        var client = await clients.TakeOnAsync("Acme " + Guid.CreateVersion7().ToString("N")[^8..]);

        await clients.MoveToAsync(client.Id, JiranisokoTech.Domain.Clients.ClientStatus.Active);

        return client.Id;
    }
}
