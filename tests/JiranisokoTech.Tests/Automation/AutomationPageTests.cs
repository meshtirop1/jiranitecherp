using System.Net;
using JiranisokoTech.Application.Automation;
using JiranisokoTech.Application.Integrations;
using JiranisokoTech.Domain.Automation;
using JiranisokoTech.Domain.Clients;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Tests.Identity;
using JiranisokoTech.Tests.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Automation;

/// <summary>
/// The rules pages, driven the way a browser drives them: every form posted for real and the
/// row read back afterwards, because the page saying "saved" is exactly what several forms in
/// this application have done while saving nothing.
/// </summary>
public class AutomationPageTests(AutomationFactory factory) : IClassFixture<AutomationFactory>
{
    [Fact]
    public async Task A_rule_is_written_and_built_up_through_its_forms()
    {
        var owner = await Browsing.SignedInAsync(factory, "rules-owner@jiranisokotech.co.ke", Roles.Owner);
        var person = await Harness.PersonAsync(factory, "Named Person");

        var written = await Browsing.PressAsync(owner, "/automation", "write", new Dictionary<string, string>
        {
            ["Input.Name"] = "Joiners, page test",
            ["Input.Trigger"] = nameof(EmployeeHired),
            ["Input.Description"] = "Written from the page.",
        });

        Browsing.Accepted(written);
        Assert.Equal(HttpStatusCode.Found, written.StatusCode);

        var address = written.Headers.Location!.OriginalString;
        var id = Guid.Parse(address.Split('/')[^1]);

        Browsing.Accepted(await Browsing.PressAsync(owner, address, "condition", new Dictionary<string, string>
        {
            ["Condition.Field"] = nameof(EmployeeHired.StartsOn),
            ["Condition.Operator"] = nameof(ConditionOperator.AtLeast),
            ["Condition.Value"] = "2026-01-01",
        }));

        Browsing.Accepted(await Browsing.PressAsync(owner, address, "work", new Dictionary<string, string>
        {
            ["Work.Title"] = "Accounts for {FullName}",
            ["Work.Detail"] = "Email and Git.",
            ["Work.Who"] = $"person:{person}",
            ["Work.Where"] = "",
            ["Work.Days"] = "3",
        }));

        Browsing.Accepted(await Browsing.PressAsync(owner, address, "notify", new Dictionary<string, string>
        {
            ["Notice.Text"] = "{FullName} starts on {StartsOn}.",
            ["Notice.Who"] = "role:" + Roles.HumanResources,
        }));

        Browsing.Accepted(await Browsing.PressAsync(owner, address, "email", new Dictionary<string, string>
        {
            ["Mail.Subject"] = "Welcome {FullName}",
            ["Mail.Body"] = "Please get ready.",
            ["Mail.Who"] = "manager:EmployeeId",
        }));

        Browsing.Accepted(await Browsing.PressAsync(owner, address, "checklist", new Dictionary<string, string>
        {
            ["Checklist.Lines"] = "Laptop prepared\nDesk ready",
            ["Checklist.Who"] = "field:EmployeeId",
        }));

        Browsing.Accepted(await Browsing.PressAsync(owner, address, "details", new Dictionary<string, string>
        {
            ["Details.Name"] = "Joiners, renamed on the page",
            ["Details.Description"] = "Changed.",
            ["Details.DelayMinutes"] = "90",
        }));

        Browsing.Accepted(await Browsing.PressAsync(owner, address, "switch-on"));

        var rule = await RuleAsync(id);

        Assert.True(rule.IsOn);
        Assert.Equal("Joiners, renamed on the page", rule.Name);
        Assert.Equal(90, rule.DelayMinutes);

        var condition = Assert.Single(rule.Conditions);
        Assert.Equal(ConditionOperator.AtLeast, condition.Operator);
        Assert.Equal("2026-01-01", condition.Value);

        Assert.Equal(
            [AutomationActionKind.RaiseWork, AutomationActionKind.Notify, AutomationActionKind.Email,
             AutomationActionKind.OnboardingSteps],
            rule.Actions.Select(one => one.Kind));
        Assert.Equal($"person:{person}", rule.Actions[0].Who);
        Assert.Equal(3, rule.Actions[0].Days);
        Assert.Equal("role:hr", rule.Actions[1].Who);
        Assert.Equal("Laptop prepared\nDesk ready", rule.Actions[3].Body);

        Browsing.Accepted(await Browsing.PressAsync(owner, address, $"drop-condition-{condition.Id}"));
        Browsing.Accepted(await Browsing.PressAsync(owner, address, $"drop-action-{rule.Actions[2].Id}"));
        Browsing.Accepted(await Browsing.PressAsync(owner, address, "switch-off"));

        rule = await RuleAsync(id);

        Assert.False(rule.IsOn);
        Assert.Empty(rule.Conditions);
        Assert.DoesNotContain(rule.Actions, one => one.Kind == AutomationActionKind.Email);
    }

    /// <summary>
    /// The details form is seeded with the stored values on a GET and left alone on a POST —
    /// the fault FirstLook exists for, where a page overwrote what was posted and said "saved".
    /// </summary>
    [Fact]
    public async Task The_details_form_shows_what_is_stored_and_keeps_what_is_posted()
    {
        var owner = await Browsing.SignedInAsync(factory, "rules-owner@jiranisokotech.co.ke", Roles.Owner);
        var id = await Harness.RuleAsync(factory, "Seeded name", nameof(ClientTakenOn),
            (_, _) => Task.CompletedTask, on: false);

        var page = await (await owner.GetAsync($"/automation/{id}")).Content.ReadAsStringAsync();
        Assert.Contains("value=\"Seeded name\"", page);

        Browsing.Accepted(await Browsing.PressAsync(owner, $"/automation/{id}", "details", new Dictionary<string, string>
        {
            ["Details.Name"] = "Posted name",
            ["Details.DelayMinutes"] = "0",
        }));

        Assert.Equal("Posted name", (await RuleAsync(id)).Name);
    }

    /// <summary>
    /// The optional boxes left as a browser leaves them: an empty number, "Nobody yet" and "No
    /// project", each posted as an empty string rather than left out. An optional field that
    /// refuses "" refuses the whole form, silently, which is the [EmailAddress] fault.
    /// </summary>
    [Fact]
    public async Task Work_with_nothing_optional_filled_in_is_accepted()
    {
        var owner = await Browsing.SignedInAsync(factory, "rules-owner@jiranisokotech.co.ke", Roles.Owner);
        var id = await Harness.RuleAsync(factory, "Plain work", nameof(ClientTakenOn),
            (_, _) => Task.CompletedTask, on: false);

        Browsing.Accepted(await Browsing.PressAsync(owner, $"/automation/{id}", "work", new Dictionary<string, string>
        {
            ["Work.Title"] = "Welcome {Name}",
            ["Work.Detail"] = "",
            ["Work.Who"] = "",
            ["Work.Where"] = "",
            ["Work.Days"] = "",
        }));

        var action = Assert.Single((await RuleAsync(id)).Actions);

        Assert.Null(action.Who);
        Assert.Null(action.Where);
        Assert.Null(action.Days);
    }

    [Fact]
    public async Task A_webhook_is_chosen_from_the_subscriptions_and_choosing_none_is_refused_in_words()
    {
        var owner = await Browsing.SignedInAsync(factory, "rules-owner@jiranisokotech.co.ke", Roles.Owner);

        var subscription = await Harness.InScopeAsync(factory, async services =>
            (await services.GetRequiredService<SubscriptionService>()
                .AddAsync("Chosen", "https://chosen.example.com/hook", ["ClientTakenOn"])).Subscription.Id);

        var id = await Harness.RuleAsync(factory, "Hooked", nameof(ClientTakenOn),
            (_, _) => Task.CompletedTask, on: false);

        var none = await Browsing.PressAsync(owner, $"/automation/{id}", "webhook", new Dictionary<string, string>
        {
            ["Hook.SubscriptionId"] = "",
        });

        Assert.Contains("Choose a subscription.", await none.Content.ReadAsStringAsync());

        Browsing.Accepted(await Browsing.PressAsync(owner, $"/automation/{id}", "webhook", new Dictionary<string, string>
        {
            ["Hook.SubscriptionId"] = subscription.ToString(),
        }));

        Assert.Equal(subscription, Assert.Single((await RuleAsync(id)).Actions).SubscriptionId);
    }

    [Fact]
    public async Task A_condition_that_could_never_be_read_is_refused_on_the_page_in_words()
    {
        var owner = await Browsing.SignedInAsync(factory, "rules-owner@jiranisokotech.co.ke", Roles.Owner);
        var id = await Harness.RuleAsync(factory, "Refusing", nameof(EmployeeHired),
            (_, _) => Task.CompletedTask, on: false);

        var refused = await Browsing.PressAsync(owner, $"/automation/{id}", "condition", new Dictionary<string, string>
        {
            ["Condition.Field"] = nameof(EmployeeHired.StartsOn),
            ["Condition.Operator"] = nameof(ConditionOperator.Is),
            ["Condition.Value"] = "next Tuesday",
        });

        var html = await refused.Content.ReadAsStringAsync();

        Assert.Contains("Write it as 2026-10-01", html);
        Assert.Empty((await RuleAsync(id)).Conditions);

        var nothingToDo = await Browsing.PressAsync(owner, $"/automation/{id}", "switch-on");

        Assert.Contains("Give the rule something to do first", await nothingToDo.Content.ReadAsStringAsync());
        Assert.False((await RuleAsync(id)).IsOn);
    }

    [Fact]
    public async Task A_rule_that_never_fired_can_be_deleted_and_a_shipped_one_cannot()
    {
        var owner = await Browsing.SignedInAsync(factory, "rules-owner@jiranisokotech.co.ke", Roles.Owner);
        var id = await Harness.RuleAsync(factory, "Short-lived", nameof(EmployeeHired),
            (_, _) => Task.CompletedTask, on: false);

        Browsing.Accepted(await Browsing.PressAsync(owner, $"/automation/{id}", "delete"));

        await Harness.InScopeAsync(factory, async services =>
            Assert.False(await services.GetRequiredService<AppDbContext>().AutomationRules.AnyAsync(one => one.Id == id)));

        var shipped = await Harness.InScopeAsync(factory, async services =>
            (await services.GetRequiredService<AppDbContext>().AutomationRules.AsNoTracking()
                .SingleAsync(one => one.TemplateKey == Templates.NewClient)).Id);

        var page = await (await owner.GetAsync($"/automation/{shipped}")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("name=\"_handler\" value=\"delete\"", page);
        Assert.Contains("Shipped with the application.", page);
    }

    /// <summary>
    /// Somebody who may read the rules sees them and their history, and cannot change one: the
    /// forms are not drawn, and a post made by hand to a form that is not drawn changes nothing.
    /// </summary>
    [Fact]
    public async Task Somebody_who_may_only_read_rules_cannot_change_one()
    {
        var auditor = await Browsing.SignedInAsync(factory, "rules-auditor@jiranisokotech.co.ke", Roles.Auditor);
        var id = await Harness.RuleAsync(factory, "Read only", nameof(EmployeeHired), (automation, rule) =>
            automation.NotifyAsync(rule, "Hello", "role:hr"), on: false);

        var list = await auditor.GetAsync("/automation");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.DoesNotContain("Write a rule", await list.Content.ReadAsStringAsync());

        var page = await auditor.GetAsync($"/automation/{id}");
        var html = await page.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Read only", html);
        Assert.DoesNotContain("Switch it on", html);

        var forged = await auditor.PostAsync($"/automation/{id}", new FormUrlEncodedContent(
            HtmlForm.Fill(html, new Dictionary<string, string> { ["_handler"] = "switch-on" })));

        Assert.NotEqual(HttpStatusCode.Found, forged.StatusCode);
        Assert.False((await RuleAsync(id)).IsOn);
    }

    [Fact]
    public async Task Somebody_without_the_permission_is_not_shown_the_rules()
    {
        var developer = await Browsing.SignedInAsync(factory, "rules-developer@jiranisokotech.co.ke", Roles.Developer);

        var response = await developer.GetAsync("/automation");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Every control on the busiest version of the rule page is named for a screen reader. The
    /// page sweep cannot reach it, because its address needs a rule's identifier.
    /// </summary>
    [Fact]
    public async Task Every_control_on_the_rule_page_has_a_label()
    {
        var owner = await Browsing.SignedInAsync(factory, "rules-owner@jiranisokotech.co.ke", Roles.Owner);

        await Harness.InScopeAsync(factory, services =>
            services.GetRequiredService<SubscriptionService>()
                .AddAsync("Labels", "https://labels.example.com/hook", ["ClientTakenOn"]));

        var id = await Harness.RuleAsync(factory, "Labelled", nameof(EmployeeHired),
            (_, _) => Task.CompletedTask, on: false);

        var html = await (await owner.GetAsync($"/automation/{id}")).Content.ReadAsStringAsync();

        Assert.Contains("name=\"_handler\" value=\"webhook\"", html);
        Assert.Empty(EveryPageOpens.Unlabelled(html));
        Assert.Empty(EveryPageOpens.Unlabelled(
            await (await owner.GetAsync("/automation")).Content.ReadAsStringAsync()));
    }

    private Task<AutomationRule> RuleAsync(Guid id) =>
        Harness.InScopeAsync(factory, services =>
            services.GetRequiredService<AppDbContext>().AutomationRules.AsNoTracking()
                .SingleAsync(one => one.Id == id));
}
