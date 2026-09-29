using JiranisokoTech.Application.Ai;
using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Infrastructure.Ai;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Ai;

/// <summary>
/// Section 36's one hard rule: a user must never receive information they are not authorised to
/// access — asserted on what was sent to the model, not on what the page said.
/// </summary>
/// <remarks>
/// The model is the thing that could leak. If a record reaches it, no instruction can be relied on
/// to keep it from the answer. So these tests read <see cref="FakeModel.Received"/>, the exact
/// requests that would have left the building, and assert that a refused lookup's records are not
/// in any of them.
/// </remarks>
public class AssistantTests(AiFactory factory) : IClassFixture<AiFactory>
{
    private async Task<AssistantAnswer> AskAsync(string question, Asker asker)
    {
        using var scope = factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<Assistant>().AskAsync(question, asker);
    }

    [Fact]
    public async Task A_refused_lookup_never_sends_the_records_to_the_model()
    {
        factory.Model.Reset();
        await AiSetup.InvoiceAsync(factory, "Kilimanjaro Roasters Refused");

        // A tech lead may ask the assistant and may not see invoices.
        var lead = await AiSetup.PersonAsync(factory, "lead-refused@jiranisokotech.co.ke", Roles.TechLead);

        factory.Model
            .ThenLooksUp("invoices", """{"unpaid_only":false,"client":""}""")
            .ThenSays("You do not have access to invoices.");

        var answer = await AskAsync("Show me unpaid invoices", lead.As(Roles.TechLead));

        Assert.Equal(AnswerStatus.Answered, answer.Status);
        Assert.True(Assert.Single(answer.Lookups).Refused);

        Assert.DoesNotContain("Kilimanjaro Roasters Refused", factory.Model.Everything);
        Assert.Contains($"does not hold {Permissions.InvoicesView}", factory.Model.Everything);
    }

    /// <summary>
    /// The other half, so the refusal above is known to be the permission and not a lookup that
    /// never finds anything.
    /// </summary>
    [Fact]
    public async Task The_same_lookup_sends_the_records_for_somebody_who_may_see_them()
    {
        factory.Model.Reset();
        var number = await AiSetup.InvoiceAsync(factory, "Kilimanjaro Roasters Allowed");

        var finance = await AiSetup.PersonAsync(factory, "finance-allowed@jiranisokotech.co.ke", Roles.FinanceManager);

        factory.Model
            .ThenLooksUp("invoices", """{"unpaid_only":false,"client":"Kilimanjaro Roasters Allowed"}""")
            .ThenSays("There is one draft.");

        var answer = await AskAsync("What have we invoiced Kilimanjaro?", finance.As(Roles.FinanceManager));

        Assert.False(Assert.Single(answer.Lookups).Refused);
        Assert.Contains(number, factory.Model.Everything);
        Assert.Contains("Kilimanjaro Roasters Allowed", factory.Model.Everything);
    }

    /// <summary>
    /// Reach, not only permission. Holding projects.view_member is permission to see the projects
    /// you are on — so a project you are not on must answer exactly as one that does not exist.
    /// </summary>
    [Fact]
    public async Task A_project_out_of_reach_answers_as_though_it_did_not_exist()
    {
        factory.Model.Reset();

        var head = await AiSetup.PersonAsync(factory, "head-reach@jiranisokotech.co.ke", Roles.DepartmentHead);
        var member = await AiSetup.PersonAsync(factory, "member-reach@jiranisokotech.co.ke", Roles.Developer);
        var outsider = await AiSetup.PersonAsync(factory, "outsider-reach@jiranisokotech.co.ke", Roles.Developer);

        var project = await AiSetup.ProjectAsync(factory, "Mobile money reach");
        await AiSetup.ItemAsync(factory, "Reconcile the till reach", project, head.Employee, member.Employee);
        await AiSetup.ItemAsync(factory, "Somebody elses secret reach", project, head.Employee);

        // A developer's permissions, plus asking: the combination the reach rule has to hold for.
        var asking = Roles.PermissionsFor(Roles.Developer).Append(Permissions.AiAsk).ToList();

        factory.Model.ThenLooksUp("project", """{"project":"Mobile money reach"}""").ThenSays("Nothing.");
        await AskAsync("How is mobile money going?", outsider.Holding(asking));

        Assert.DoesNotContain("Reconcile the till reach", factory.Model.Everything);
        Assert.Contains("No project the person may see matches", factory.Model.Everything);

        factory.Model.Reset();
        factory.Model.ThenLooksUp("project", """{"project":"Mobile money reach"}""").ThenSays("One item.");
        await AskAsync("How is mobile money going?", member.Holding(asking));

        // On it, so it is shown — but only their own work, the board's rule for tasks.view_own.
        Assert.Contains("Reconcile the till reach", factory.Model.Everything);
        Assert.DoesNotContain("Somebody elses secret reach", factory.Model.Everything);
        // And told what it was not shown, so an absence reads as "withheld" rather than "none".
        Assert.Contains("you may see only your own work", factory.Model.Everything);
        Assert.Contains("you do not hold projects.view_all", factory.Model.Everything);
    }

    [Fact]
    public async Task Somebody_without_the_permission_sends_nothing()
    {
        factory.Model.Reset();
        var developer = await AiSetup.PersonAsync(factory, "dev-noask@jiranisokotech.co.ke", Roles.Developer);

        var answer = await AskAsync("Which projects are late?", developer.As(Roles.Developer));

        Assert.Equal(AnswerStatus.NotPermitted, answer.Status);
        Assert.Empty(factory.Model.Received);
    }

    /// <summary>
    /// No key, no fake answer. The brief forbids fake functionality, and an assistant that made
    /// something up locally when the provider was not configured would be exactly that.
    /// </summary>
    [Fact]
    public async Task With_no_key_it_says_so_and_sends_and_records_nothing()
    {
        factory.Model.Reset();
        factory.Model.IsConfigured = false;

        var lead = await AiSetup.PersonAsync(factory, "lead-nokey@jiranisokotech.co.ke", Roles.TechLead);

        var answer = await AskAsync("Which projects are late?", lead.As(Roles.TechLead));

        Assert.Equal(AnswerStatus.NotConfigured, answer.Status);
        Assert.Null(answer.Text);
        Assert.Empty(factory.Model.Received);

        await factory.InScopeAsync(async services => Assert.False(
            await services.GetRequiredService<AppDbContext>().AiExchanges
                .AnyAsync(one => one.AccountId == lead.Account)));
    }

    [Fact]
    public async Task A_provider_failure_is_said_plainly_and_recorded()
    {
        factory.Model.Reset();
        factory.Model.ThenFails(AiFailure.Overloaded, "The AI provider is overloaded at the moment.");

        var lead = await AiSetup.PersonAsync(factory, "lead-overloaded@jiranisokotech.co.ke", Roles.TechLead);

        var answer = await AskAsync("Which projects are late?", lead.As(Roles.TechLead));

        Assert.Equal(AnswerStatus.Failed, answer.Status);
        Assert.Equal("The AI provider is overloaded at the moment.", answer.Problem);
        Assert.Null(answer.Text);

        await factory.InScopeAsync(async services =>
        {
            var logged = await services.GetRequiredService<AppDbContext>().AiExchanges
                .SingleAsync(one => one.AccountId == lead.Account);

            Assert.Equal(AiOutcome.Failed, logged.Outcome);
            Assert.Equal("Which projects are late?", logged.Question);
        });
    }

    [Fact]
    public async Task Every_question_is_logged_with_what_it_looked_up()
    {
        factory.Model.Reset();
        var lead = await AiSetup.PersonAsync(factory, "lead-logged@jiranisokotech.co.ke", Roles.TechLead);

        factory.Model
            .ThenLooksUp("list_projects", "{}")
            .ThenLooksUp("invoices", """{"unpaid_only":true,"client":""}""")
            .ThenSays("Here is what I found.");

        await AskAsync("Projects and money, please", lead.As(Roles.TechLead));

        await factory.InScopeAsync(async services =>
        {
            var logged = await services.GetRequiredService<AppDbContext>().AiExchanges
                .SingleAsync(one => one.AccountId == lead.Account);

            Assert.Equal("list_projects, invoices (refused)", logged.Lookups);
            Assert.Equal(AiOutcome.Answered, logged.Outcome);
            Assert.Equal(AiFeature.Assistant, logged.Feature);
        });
    }

    [Fact]
    public async Task The_daily_allowance_stops_the_next_question_before_anything_is_sent()
    {
        factory.Model.Reset();
        var lead = await AiSetup.PersonAsync(factory, "lead-limit@jiranisokotech.co.ke", Roles.TechLead);

        await factory.InScopeAsync(async services =>
        {
            var ledger = services.GetRequiredService<AiLedger>();

            for (var used = 0; used < ledger.DailyLimit; used++)
            {
                await ledger.RecordAsync(new AiExchange(
                    DateTimeOffset.UtcNow, lead.Account, lead.Email, AiFeature.Assistant, null, "earlier",
                    string.Empty, AiOutcome.Answered, null, "fake-model", 1, 1, TimeSpan.Zero));
            }
        });

        var answer = await AskAsync("One more?", lead.As(Roles.TechLead));

        Assert.Equal(AnswerStatus.LimitReached, answer.Status);
        Assert.Empty(factory.Model.Received);
    }

    /// <summary>
    /// Section 36 asks for confirmation before anything destructive; here nothing at all is done
    /// without it. A proposed task is a proposal until a person presses the button.
    /// </summary>
    [Fact]
    public async Task Proposing_a_task_creates_nothing()
    {
        factory.Model.Reset();
        var head = await AiSetup.PersonAsync(factory, "head-propose@jiranisokotech.co.ke", Roles.DepartmentHead);
        var asker = head.As(Roles.DepartmentHead);

        factory.Model
            .ThenLooksUp("propose_task", """{"title":"Fix the failing callback proposal","project":""}""")
            .ThenSays("I have proposed a task.");

        var answer = await AskAsync("Create a task to fix the callback", asker);

        Assert.Equal("Fix the failing callback proposal", answer.Proposal?.Title);

        await factory.InScopeAsync(async services => Assert.False(
            await services.GetRequiredService<AppDbContext>().WorkItems
                .AnyAsync(one => one.Title == "Fix the failing callback proposal")));
    }

    /// <summary>
    /// The model's previous reply goes back exactly as it came, or the provider refuses the
    /// conversation — its reasoning is only valid unchanged.
    /// </summary>
    [Fact]
    public async Task The_models_own_reply_is_handed_back_verbatim_with_the_lookup_results()
    {
        factory.Model.Reset();
        var lead = await AiSetup.PersonAsync(factory, "lead-verbatim@jiranisokotech.co.ke", Roles.TechLead);
        const string raw = """[{"type":"thinking","thinking":"","signature":"abc"},{"type":"tool_use","id":"toolu_1","name":"list_projects","input":{}}]""";

        factory.Model
            .Then(_ => new ModelReply(ReplyEnd.WantsTools, string.Empty, [new ToolCall("toolu_1", "list_projects", "{}")], raw, 1, 1, "fake-model"))
            .ThenSays("Done.");

        await AskAsync("List the projects", lead.As(Roles.TechLead));

        var second = factory.Model.Received.ElementAt(1);

        Assert.Equal(raw, Assert.IsType<Replied>(second.Conversation[1]).Raw);
        Assert.Equal("toolu_1", Assert.Single(Assert.IsType<ToolResults>(second.Conversation[2]).Results).CallId);
    }
}
