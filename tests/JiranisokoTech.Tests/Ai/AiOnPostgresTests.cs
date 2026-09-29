using JiranisokoTech.Infrastructure.Ai;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Tests.Postgres;
using JiranisokoTech.Tests.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Ai;

/// <summary>
/// The parts of the AI features that touch the database, against PostgreSQL.
/// </summary>
/// <remarks>
/// SQLite forgives two things PostgreSQL does not — timestamps with an offset, and queries that
/// finish before an await yields — and both are in play here: the usage log is written with a
/// timestamp and counted by one for the daily limit, and the project reading compares build and
/// deployment times against a window.
/// </remarks>
public class AiOnPostgresTests
{
    [PostgresFact]
    public async Task A_question_is_logged_and_counted_against_the_allowance_on_postgres()
    {
        using var factory = new PostgresAiFactory();

        var lead = await AiSetup.PersonAsync(factory, "lead-pg@jiranisokotech.co.ke", Roles.TechLead);

        factory.Model
            .ThenLooksUp("invoices", """{"unpaid_only":true,"client":""}""")
            .ThenSays("You cannot see invoices.");

        var response = await Browsing.PressAsync(lead.Browser, "/assistant", "ask",
            extra: [new("Input.Question", "Unpaid invoices, on PostgreSQL")]);
        var html = await response.Content.ReadAsStringAsync();

        Browsing.Accepted(response);
        Assert.Contains("Refused: you may not see this", html);

        await factory.InScopeAsync(async services =>
        {
            var logged = await services.GetRequiredService<AppDbContext>().AiExchanges
                .SingleAsync(one => one.AccountId == lead.Account);

            Assert.Equal("invoices (refused)", logged.Lookups);

            var ledger = services.GetRequiredService<AiLedger>();
            Assert.Equal(ledger.DailyLimit - 1, await ledger.RemainingTodayAsync(lead.Account));
        });
    }

    [PostgresFact]
    public async Task A_project_reading_opens_and_is_read_on_postgres()
    {
        using var factory = new PostgresAiFactory();

        var head = await AiSetup.PersonAsync(factory, "head-pg@jiranisokotech.co.ke", Roles.DepartmentHead);
        var project = await AiSetup.ProjectAsync(factory, "Reading on PostgreSQL");
        await AiSetup.ItemAsync(factory, "A job on PostgreSQL", project, head.Employee, head.Employee);

        factory.Model.ThenSays("""
            {"summary":"Read on PostgreSQL.","judgements":[{"area":"Schedule","assessment":"Cannot tell","because":"No due date."}],
             "blockers":[],"main_risk":"No date."}
            """);

        var response = await Browsing.PressAsync(head.Browser, $"/projects/{project}/reading", "read");
        var html = await response.Content.ReadAsStringAsync();

        Browsing.Accepted(response);
        Assert.Contains("A job on PostgreSQL", html);
        Assert.Contains("Read on PostgreSQL.", html);
    }
}
