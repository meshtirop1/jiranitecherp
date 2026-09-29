using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Automation;
using JiranisokoTech.Application.Business;
using JiranisokoTech.Application.Integrations;
using JiranisokoTech.Application.People;
using JiranisokoTech.Application.Work;
using JiranisokoTech.Domain.Automation;
using JiranisokoTech.Domain.Clients;
using JiranisokoTech.Domain.Notices;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.Automation;
using JiranisokoTech.Infrastructure.Messaging;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Automation;

/// <summary>
/// The engine inside the running application: an event through the outbox, a rule matched,
/// its actions carried out through the real services, and the record of what happened.
/// </summary>
/// <remarks>
/// One application per test, because a rule switched on in one test would fire on the events
/// of every other — and an assertion about how many runs a rule made would then depend on the
/// order xUnit happened to choose.
/// </remarks>
public class EngineTests
{
    [Fact]
    public async Task A_rule_fires_through_the_outbox_and_the_trail_names_the_rule()
    {
        using var factory = new AutomationFactory();
        var seller = await Harness.PersonAsync(factory, "Achieng Seller", Roles.Sales);

        var rule = await Harness.RuleAsync(factory, "Welcome", nameof(ClientTakenOn), async (automation, id) =>
        {
            await automation.WhenAsync(id, nameof(ClientTakenOn.Name), ConditionOperator.Contains, "Acme");
            await automation.RaiseWorkAsync(id, "Onboard {Name} ({Code})", "Call them first.", null, null, 5);
            await automation.NotifyAsync(id, "{Name} has been taken on.", "role:" + Roles.Sales);
        });

        var acme = await TakeOnAsync(factory, "Acme Holdings");
        await TakeOnAsync(factory, "Other Company");

        await Harness.DrainAsync(factory);

        var run = Assert.Single(await Harness.RunsAsync(factory, rule));
        Assert.Equal(AutomationRunStatus.Done, run.Status);
        Assert.All(run.Steps, step => Assert.True(step.IsDone && !step.Refused, step.Outcome));

        await Harness.InScopeAsync(factory, async services =>
        {
            var database = services.GetRequiredService<AppDbContext>();
            var today = services.GetRequiredService<IClock>().Today;

            var item = await database.WorkItems.AsNoTracking()
                .SingleAsync(one => one.Title == "Onboard Acme Holdings (acme-holdings)");

            Assert.Equal(Guid.Empty, item.RaisedById);
            Assert.Equal(today.AddDays(5), item.DueOn);
            Assert.StartsWith("Call them first.", item.Detail);
            Assert.Contains("Raised by the automation rule “Welcome”", item.Detail);

            // The one thing an audit trail of a rule's work has to say, and without the
            // fallback in AppDbContext it would say nothing at all.
            var raised = await database.AuditEntries.AsNoTracking()
                .SingleAsync(one => one.SubjectId == item.Id && one.Action == "work_item.added");
            Assert.Null(raised.ActorId);
            Assert.Equal("Automation rule: Welcome", raised.ActorName);

            var notice = await database.Notices.AsNoTracking().SingleAsync(one => one.ForEmployeeId == seller);
            Assert.Equal(NoticeKind.Automation, notice.Kind);
            Assert.Equal("Acme Holdings has been taken on.", notice.Subject);
            Assert.Equal($"/clients/{acme}", notice.Link);
        });
    }

    /// <summary>
    /// A rule that raises work whenever work is raised fires once, on the work a person raised,
    /// and is held back from its own — which would otherwise raise another, and another.
    /// </summary>
    [Fact]
    public async Task A_rule_is_not_fired_by_its_own_work()
    {
        using var factory = new AutomationFactory();

        var rule = await Harness.RuleAsync(factory, "Echo", nameof(WorkItemRaised), (automation, id) =>
            automation.RaiseWorkAsync(id, "Follow up: {Title}", null, null, null, null));

        await Harness.InScopeAsync(factory, services =>
            services.GetRequiredService<WorkService>().RaiseAsync("Fix the login page", Guid.Empty));

        await Harness.DrainAsync(factory);

        var runs = await Harness.RunsAsync(factory, rule);

        Assert.Equal(2, runs.Count);
        Assert.Equal(AutomationRunStatus.Done, runs[0].Status);
        Assert.Equal(AutomationRunStatus.Suppressed, runs[1].Status);
        Assert.Contains("own actions", runs[1].Error);
        Assert.Equal([rule], runs[1].ChainIds);

        await Harness.InScopeAsync(factory, async services =>
        {
            var titles = await services.GetRequiredService<AppDbContext>().WorkItems.AsNoTracking()
                .Select(one => one.Title).ToListAsync();

            Assert.Equal(2, titles.Count);
            Assert.Contains("Follow up: Fix the login page", titles);
        });
    }

    /// <summary>
    /// One rule's work may fire another — that is what a chain is — and the second run carries
    /// the first rule in its chain, which is what stops the pair looping.
    /// </summary>
    [Fact]
    public async Task One_rule_can_fire_another_and_the_second_knows_why()
    {
        using var factory = new AutomationFactory();
        var seller = await Harness.PersonAsync(factory, "Achieng Seller", Roles.Sales);

        var first = await Harness.RuleAsync(factory, "Client work", nameof(ClientTakenOn), (automation, id) =>
            automation.RaiseWorkAsync(id, "Onboard {Name}", null, null, null, null));

        var second = await Harness.RuleAsync(factory, "Tell sales about new work", nameof(WorkItemRaised), (automation, id) =>
            automation.NotifyAsync(id, "New work: {Title}", "role:" + Roles.Sales));

        await TakeOnAsync(factory, "Chained Ltd");
        await Harness.DrainAsync(factory);

        Assert.Equal(AutomationRunStatus.Done, Assert.Single(await Harness.RunsAsync(factory, first)).Status);

        var told = Assert.Single(await Harness.RunsAsync(factory, second));
        Assert.Equal(AutomationRunStatus.Done, told.Status);
        Assert.Equal([first], told.ChainIds);

        await Harness.InScopeAsync(factory, async services =>
            Assert.True(await services.GetRequiredService<AppDbContext>().Notices
                .AnyAsync(one => one.ForEmployeeId == seller && one.Subject == "New work: Onboard Chained Ltd")));
    }

    [Fact]
    public async Task A_chain_that_is_already_three_rules_deep_goes_no_further()
    {
        using var factory = new AutomationFactory();

        var rule = await Harness.RuleAsync(factory, "Deep", nameof(ClientTakenOn), (automation, id) =>
            automation.RaiseWorkAsync(id, "Onboard {Name}", null, null, null, null));

        Guid[] chain = [Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()];

        await MatchAsync(factory, new ClientTakenOn(Guid.NewGuid(), "Deep Ltd", "deep"), Guid.CreateVersion7(), chain);

        var run = Assert.Single(await Harness.RunsAsync(factory, rule));
        Assert.Equal(AutomationRunStatus.Suppressed, run.Status);
        Assert.Contains("Chains stop at 3", run.Error);
    }

    /// <summary>
    /// The outbox runs the matcher again whenever another handler of the same event fails.
    /// Seeing one message twice must not raise the work twice.
    /// </summary>
    [Fact]
    public async Task The_same_event_seen_twice_makes_one_run()
    {
        using var factory = new AutomationFactory();

        var rule = await Harness.RuleAsync(factory, "Once", nameof(ClientTakenOn), (automation, id) =>
            automation.RaiseWorkAsync(id, "Onboard {Name}", null, null, null, null));

        var message = Guid.CreateVersion7();
        var taken = new ClientTakenOn(Guid.NewGuid(), "Twice Ltd", "twice");

        await MatchAsync(factory, taken, message, []);
        await MatchAsync(factory, taken, message, []);

        Assert.Single(await Harness.RunsAsync(factory, rule));
    }

    [Fact]
    public async Task A_rule_that_has_fired_too_often_this_hour_is_held_back()
    {
        using var factory = new AutomationFactory();

        var rule = await Harness.RuleAsync(factory, "Busy", nameof(ClientTakenOn), (automation, id) =>
            automation.RaiseWorkAsync(id, "Onboard {Name}", null, null, null, null));

        await Harness.InScopeAsync(factory, async services =>
        {
            var database = services.GetRequiredService<AppDbContext>();
            var stored = await database.AutomationRules.AsNoTracking().SingleAsync(one => one.Id == rule);
            var now = services.GetRequiredService<IClock>().Now;

            for (var i = 0; i < AutomationRun.MostPerHour; i++)
            {
                database.AutomationRuns.Add(AutomationRun.Held(
                    stored, Guid.CreateVersion7(), "earlier", [], "stand-in", now));
            }

            await database.SaveChangesAsync();

            // Held runs do not count against the limit, which is the point of recording them:
            // a burst that was held back must not also hold back the next hour.
            await database.AutomationRuns
                .Where(one => one.RuleId == rule)
                .ExecuteUpdateAsync(set => set.SetProperty(one => one.Status, AutomationRunStatus.Done));
        });

        await MatchAsync(factory, new ClientTakenOn(Guid.NewGuid(), "One Too Many", "one-too-many"), Guid.CreateVersion7(), []);

        var latest = (await Harness.RunsAsync(factory, rule)).Last();
        Assert.Equal(AutomationRunStatus.Suppressed, latest.Status);
        Assert.Contains("already fired 60 times", latest.Error);
    }

    /// <summary>
    /// A service's refusal is final: the same work on the same closed project would be refused
    /// on every retry, so it is recorded against the step, the other actions go ahead, and the
    /// outbox is not asked to try again.
    /// </summary>
    [Fact]
    public async Task A_refusal_is_recorded_and_the_rest_of_the_rule_goes_ahead()
    {
        using var factory = new AutomationFactory();
        var seller = await Harness.PersonAsync(factory, "Achieng Seller", Roles.Sales);

        var project = await Harness.InScopeAsync(factory, async services =>
            (await services.GetRequiredService<WorkService>().BeginProjectAsync("Closed thing")).Id);

        var rule = await Harness.RuleAsync(factory, "Into a closed project", nameof(ClientTakenOn), async (automation, id) =>
        {
            await automation.RaiseWorkAsync(id, "Onboard {Name}", null, null, $"project:{project}", null);
            await automation.NotifyAsync(id, "{Name} taken on", "role:" + Roles.Sales);
        });

        await Harness.InScopeAsync(factory, services =>
            services.GetRequiredService<WorkService>().CancelProjectAsync(project, "Lost the bid"));

        await TakeOnAsync(factory, "Refused Ltd");
        await Harness.DrainAsync(factory);

        var run = Assert.Single(await Harness.RunsAsync(factory, rule));

        Assert.Equal(AutomationRunStatus.DoneWithRefusals, run.Status);
        Assert.Equal(0, run.Attempts);
        Assert.True(run.Steps[0].Refused);
        Assert.Contains("cannot be added", run.Steps[0].Outcome);
        Assert.False(run.Steps[1].Refused);

        await Harness.InScopeAsync(factory, async services =>
        {
            var database = services.GetRequiredService<AppDbContext>();

            Assert.True(await database.Notices.AnyAsync(one => one.ForEmployeeId == seller));
            Assert.Equal(0, await database.Outbox.CountAsync(one => one.Error != null));
        });
    }

    /// <summary>
    /// A mail server that does not answer is retried by the outbox, and the retry does not raise
    /// the work item that was raised before the failure a second time.
    /// </summary>
    [Fact]
    public async Task A_failure_is_retried_by_the_outbox_without_doing_the_work_twice()
    {
        using var factory = new AutomationFactory();
        await Harness.PersonAsync(factory, "Achieng Seller", Roles.Sales);

        var rule = await Harness.RuleAsync(factory, "Work then mail", nameof(ClientTakenOn), async (automation, id) =>
        {
            await automation.RaiseWorkAsync(id, "Onboard {Name}", null, null, null, null);
            await automation.EmailAsync(id, "{Name} is ours", "Say hello to {Name}.", "role:" + Roles.Sales);
        });

        factory.Mail.FailNext(1);
        await TakeOnAsync(factory, "Retry Ltd");

        await Harness.PassAsync(factory); // the event: the rule matches
        await Harness.PassAsync(factory); // the run: work raised, mail fails

        var failed = Assert.Single(await Harness.RunsAsync(factory, rule));
        Assert.Equal(AutomationRunStatus.Retrying, failed.Status);
        Assert.Equal(1, failed.Attempts);
        Assert.Contains("mail server did not answer", failed.Error);
        Assert.True(failed.Steps[0].IsDone);
        Assert.NotNull(failed.Steps[0].MadeId);

        await Harness.DrainAsync(factory);

        var done = Assert.Single(await Harness.RunsAsync(factory, rule));
        Assert.Equal(AutomationRunStatus.Done, done.Status);
        Assert.Single(factory.Mail.Sent);
        Assert.Contains("automation rule \"Work then mail\"", factory.Mail.Sent.Single().TextBody);

        await Harness.InScopeAsync(factory, async services =>
            Assert.Equal(1, await services.GetRequiredService<AppDbContext>().WorkItems
                .CountAsync(one => one.Title == "Onboard Retry Ltd")));
    }

    /// <summary>
    /// Giving up happens once, in the outbox, and the run says so at the same moment — then
    /// putting the message back from the machinery screen carries on where it stopped.
    /// </summary>
    [Fact]
    public async Task A_run_that_keeps_failing_gives_up_with_the_outbox_and_can_be_put_back()
    {
        using var factory = new AutomationFactory();
        await Harness.PersonAsync(factory, "Achieng Seller", Roles.Sales);

        var rule = await Harness.RuleAsync(factory, "Mail only", nameof(ClientTakenOn), (automation, id) =>
            automation.EmailAsync(id, "{Name} is ours", "Say hello.", "role:" + Roles.Sales));

        factory.Mail.FailNext(100);
        await TakeOnAsync(factory, "Hopeless Ltd");
        await Harness.DrainAsync(factory);

        var run = Assert.Single(await Harness.RunsAsync(factory, rule));
        Assert.Equal(AutomationRunStatus.GaveUp, run.Status);
        Assert.Equal(2, run.Attempts);

        var abandoned = await Harness.InScopeAsync(factory, services =>
            services.GetRequiredService<AppDbContext>().Outbox.AsNoTracking()
                .SingleAsync(one => one.Type == nameof(AutomationRunDue)));
        Assert.NotNull(abandoned.AbandonedAt);

        factory.Mail.FailNext(0);

        await Harness.InScopeAsync(factory, services =>
            services.GetRequiredService<OutboxAdministration>().ReviveAsync(abandoned.Id));
        await Harness.DrainAsync(factory);

        Assert.Equal(AutomationRunStatus.Done, Assert.Single(await Harness.RunsAsync(factory, rule)).Status);
        Assert.Single(factory.Mail.Sent);
    }

    [Fact]
    public async Task A_delayed_run_waits_for_its_time_and_a_rule_switched_off_meanwhile_does_not_act()
    {
        using var factory = new AutomationFactory();

        var later = await Harness.RuleAsync(factory, "An hour later", nameof(ClientTakenOn), async (automation, id) =>
        {
            await automation.DescribeAsync(id, "An hour later", null, 60);
            await automation.RaiseWorkAsync(id, "Check in with {Name}", null, null, null, null);
        });

        var cancelled = await Harness.RuleAsync(factory, "Changed my mind", nameof(ClientTakenOn), async (automation, id) =>
        {
            await automation.DescribeAsync(id, "Changed my mind", null, 60);
            await automation.RaiseWorkAsync(id, "Never raised for {Name}", null, null, null, null);
        });

        await TakeOnAsync(factory, "Patient Ltd");
        await Harness.DrainAsync(factory);

        Assert.Equal(AutomationRunStatus.Waiting, Assert.Single(await Harness.RunsAsync(factory, later)).Status);

        await Harness.InScopeAsync(factory, services =>
            services.GetRequiredService<AutomationService>().SwitchOffAsync(cancelled));

        var clock = new TestClock { Now = DateTimeOffset.UtcNow };

        Assert.Equal("Nothing was waiting.", await ReleaseAsync(factory, clock));

        clock.Advance(TimeSpan.FromMinutes(61));
        await ReleaseAsync(factory, clock);
        await Harness.DrainAsync(factory);

        Assert.Equal(AutomationRunStatus.Done, Assert.Single(await Harness.RunsAsync(factory, later)).Status);

        var gone = Assert.Single(await Harness.RunsAsync(factory, cancelled));
        Assert.Equal(AutomationRunStatus.Cancelled, gone.Status);
        Assert.Contains("switched off", gone.Error);

        await Harness.InScopeAsync(factory, async services =>
        {
            var titles = await services.GetRequiredService<AppDbContext>().WorkItems
                .Select(one => one.Title).ToListAsync();

            Assert.Contains("Check in with Patient Ltd", titles);
            Assert.DoesNotContain("Never raised for Patient Ltd", titles);
        });
    }

    /// <summary>
    /// Section 31's own example, end to end: an invoice goes overdue, the daily job says so
    /// once per rung, and the shipped rule puts the chase on the board.
    /// </summary>
    [Fact]
    public async Task An_overdue_invoice_is_announced_once_per_rung_and_only_to_a_listening_rule()
    {
        using var factory = new AutomationFactory();

        var (invoice, number, dueOn) = await Harness.InScopeAsync(factory, async services =>
        {
            var client = await services.GetRequiredService<ClientService>().TakeOnAsync("Slow Payer Ltd");
            var invoices = services.GetRequiredService<InvoiceService>();
            var draft = await invoices.DraftAsync(client.Id, "KES");

            await invoices.AddLineAsync(draft.Id, "Discovery", 1, JiranisokoTech.Domain.Common.Money.Of(500_000, "KES"));
            await invoices.SendAsync(draft.Id);

            var sent = await services.GetRequiredService<AppDbContext>().Invoices.AsNoTracking()
                .SingleAsync(one => one.Id == draft.Id);

            return (sent.Id, sent.Number, sent.DueOn);
        });

        await Harness.DrainAsync(factory);

        var clock = new TestClock
        {
            Now = new DateTimeOffset(dueOn.AddDays(10).ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero),
        };

        Assert.StartsWith("No rule is switched on", await OverdueAsync(factory, clock));

        var template = await Harness.InScopeAsync(factory, async services =>
        {
            var id = (await services.GetRequiredService<AppDbContext>().AutomationRules.AsNoTracking()
                .SingleAsync(one => one.TemplateKey == Templates.OverdueInvoice)).Id;

            await services.GetRequiredService<AutomationService>().SwitchOnAsync(id);

            return id;
        });

        Assert.EndsWith("1 reached a new rung today.", await OverdueAsync(factory, clock));
        Assert.EndsWith("0 reached a new rung today.", await OverdueAsync(factory, clock));

        await Harness.DrainAsync(factory);

        var run = Assert.Single(await Harness.RunsAsync(factory, template));

        // Nobody here holds the finance or sales roles, so the two notices are refused in words
        // and the chase is still raised: the step that could be done was.
        Assert.Equal(AutomationRunStatus.DoneWithRefusals, run.Status);
        Assert.Equal($"{number}, 10 days overdue", run.Summary);

        await Harness.InScopeAsync(factory, async services =>
            Assert.True(await services.GetRequiredService<AppDbContext>().WorkItems
                .AnyAsync(one => one.Title == $"Chase payment of invoice {number}, 10 days overdue")));

        Assert.NotEqual(Guid.Empty, invoice);
    }

    [Fact]
    public async Task A_joiners_checklist_gets_each_line_once_however_many_rules_add_it()
    {
        using var factory = new AutomationFactory();

        foreach (var name in new[] { "Laptop list", "Laptop list again" })
        {
            await Harness.RuleAsync(factory, name, nameof(EmployeeHired), (automation, id) =>
                automation.ChecklistAsync(id, "Laptop prepared\nSigned contract returned", "field:EmployeeId"));
        }

        var joiner = await Harness.InScopeAsync(factory, async services =>
            (await services.GetRequiredService<PeopleService>()
                .HireAsync("New Joiner", DateOnly.FromDateTime(DateTime.UtcNow).AddDays(14))).Id);

        await Harness.DrainAsync(factory);

        await Harness.InScopeAsync(factory, async services =>
        {
            var list = await services.GetRequiredService<OnboardingService>().ForAsync(joiner);

            Assert.NotNull(list);
            Assert.Single(list.Steps, step => step.Name == "Laptop prepared");

            // Already on the usual list, so neither rule added it again.
            Assert.Single(list.Steps, step => step.Name == "Signed contract returned");
        });
    }

    [Fact]
    public async Task A_webhook_action_queues_a_signed_notification_to_a_subscription_that_is_on()
    {
        using var factory = new AutomationFactory();

        var (subscription, off) = await Harness.InScopeAsync(factory, async services =>
        {
            var subscriptions = services.GetRequiredService<SubscriptionService>();
            var (on, _) = await subscriptions.AddAsync("Accounts", "https://accounts.example.com/hook", ["ClientTakenOn"]);
            var (other, _) = await subscriptions.AddAsync("Old", "https://old.example.com/hook", ["ClientTakenOn"]);

            return (on.Id, other.Id);
        });

        var rule = await Harness.RuleAsync(factory, "Tell accounts", nameof(ClientTakenOn), async (automation, id) =>
        {
            await automation.WebhookAsync(id, subscription);
            await automation.WebhookAsync(id, off);
        });

        await Harness.InScopeAsync(factory, services =>
            services.GetRequiredService<SubscriptionService>().DisableAsync(off, "Retired"));

        await TakeOnAsync(factory, "Hooked Ltd");
        await Harness.DrainAsync(factory);

        var run = Assert.Single(await Harness.RunsAsync(factory, rule));
        Assert.Equal(AutomationRunStatus.DoneWithRefusals, run.Status);
        Assert.Contains("switched off", run.Steps[1].Outcome);

        await Harness.InScopeAsync(factory, async services =>
        {
            var delivery = await services.GetRequiredService<AppDbContext>().OutboundDeliveries.AsNoTracking()
                .SingleAsync(one => one.Event == "automation.ClientTakenOn");

            Assert.Equal(subscription, delivery.SubscriptionId);
            Assert.Contains("\"rule\":\"Tell accounts\"", delivery.Payload);
            Assert.Contains("Hooked Ltd", delivery.Payload);
        });
    }

    /// <summary>
    /// The manager of somebody just hired is usually nobody yet, so "tell their manager" reaches
    /// the head of the department they are joining instead.
    /// </summary>
    [Fact]
    public async Task Their_manager_is_the_head_of_their_department_until_somebody_is_named()
    {
        using var factory = new AutomationFactory();
        var head = await Harness.PersonAsync(factory, "Head Of Engineering");

        var department = await Harness.InScopeAsync(factory, async services =>
        {
            var people = services.GetRequiredService<PeopleService>();
            var opened = await people.OpenDepartmentAsync("Engineering");

            await people.AppointHeadAsync(opened.Id, head);

            return opened.Id;
        });

        await Harness.RuleAsync(factory, "Tell the manager", nameof(EmployeeHired), (automation, id) =>
            automation.NotifyAsync(id, "{FullName} joins you on {StartsOn}.", "manager:EmployeeId"));

        await Harness.InScopeAsync(factory, services =>
            services.GetRequiredService<PeopleService>().HireAsync(
                "Fresh Engineer", new DateOnly(2026, 11, 2), department));

        await Harness.DrainAsync(factory);

        await Harness.InScopeAsync(factory, async services =>
            Assert.True(await services.GetRequiredService<AppDbContext>().Notices
                .AnyAsync(one => one.ForEmployeeId == head
                    && one.Subject == "Fresh Engineer joins you on 2 Nov 2026.")));
    }

    [Fact]
    public async Task Writing_and_changing_a_rule_is_in_the_audit_trail()
    {
        using var factory = new AutomationFactory();

        var rule = await Harness.RuleAsync(factory, "Audited", nameof(ClientTakenOn), async (automation, id) =>
        {
            await automation.WhenAsync(id, nameof(ClientTakenOn.Name), ConditionOperator.IsSet, null);
            await automation.NotifyAsync(id, "Hello", "role:" + Roles.Sales);
        });

        await Harness.InScopeAsync(factory, async services =>
        {
            var actions = await services.GetRequiredService<AppDbContext>().AuditEntries.AsNoTracking()
                .Where(one => one.SubjectType.StartsWith("Automation"))
                .Select(one => one.Action)
                .ToListAsync();

            Assert.Contains("automation_rule.added", actions);
            Assert.Contains("automation_rule.modified", actions);
            Assert.Contains("automation_condition.added", actions);
            Assert.Contains("automation_action.added", actions);
        });

        Assert.NotEqual(Guid.Empty, rule);
    }

    /// <summary>
    /// What a rule's action saves carries the rule in its chain, through a fixed handler in
    /// between as well. Written directly, because it is the column the loop check reads.
    /// </summary>
    [Fact]
    public async Task An_event_saved_while_a_rule_acts_is_stamped_with_the_rule()
    {
        using var factory = new AutomationFactory();
        var rule = Guid.CreateVersion7();

        await Harness.InScopeAsync(factory, async services =>
        {
            using (Causation.Enter(new Cause(null, [rule], "Automation rule: Stamp")))
            {
                await services.GetRequiredService<ClientService>().TakeOnAsync("Stamped Ltd");
            }

            await services.GetRequiredService<ClientService>().TakeOnAsync("Unstamped Ltd");
        });

        await Harness.InScopeAsync(factory, async services =>
        {
            var stamps = await services.GetRequiredService<AppDbContext>().Outbox.AsNoTracking()
                .Where(one => one.Type == nameof(ClientTakenOn))
                .OrderBy(one => one.OccurredAt)
                .Select(one => one.Causation)
                .ToListAsync();

            Assert.Equal([rule.ToString("D"), null], stamps);
        });
    }

    private static Task<Guid> TakeOnAsync(AutomationFactory factory, string name) =>
        Harness.InScopeAsync(factory, async services =>
            (await services.GetRequiredService<ClientService>().TakeOnAsync(name)).Id);

    private static async Task MatchAsync(
        AutomationFactory factory, ClientTakenOn taken, Guid message, IReadOnlyList<Guid> chain)
    {
        using var scope = factory.Services.CreateScope();

        var matcher = new MatchRules<ClientTakenOn>(
            scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            scope.ServiceProvider.GetRequiredService<IClock>(),
            NullLogger<MatchRules<ClientTakenOn>>.Instance);

        using (Causation.Enter(new Cause(message, chain, null)))
        {
            await matcher.HandleAsync(taken);
        }
    }

    private static async Task<string> ReleaseAsync(AutomationFactory factory, TestClock clock)
    {
        using var scope = factory.Services.CreateScope();

        return await new ReleaseDelayedAutomation(
            scope.ServiceProvider.GetRequiredService<AppDbContext>(), clock).RunAsync();
    }

    private static async Task<string> OverdueAsync(AutomationFactory factory, TestClock clock)
    {
        using var scope = factory.Services.CreateScope();

        return await new AnnounceOverdueInvoices(
            scope.ServiceProvider.GetRequiredService<AppDbContext>(), clock).RunAsync();
    }
}
