using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Automation;
using JiranisokoTech.Domain.Automation;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Infrastructure.Messaging;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Tests.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace JiranisokoTech.Tests.Automation;

/// <summary>
/// The list of triggers and the shipped templates are consistent with the events they name.
/// </summary>
/// <remarks>
/// Both are data, and data that names a field by string is the kind of thing that compiles
/// cleanly and is wrong. A template whose title says {FulName} would put the braces on the
/// board; a trigger whose summary names a field its event no longer has would write the
/// braces into every run's history. Neither fails anything at the point it is written.
/// </remarks>
public class CatalogueTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    private static readonly DateTimeOffset At = new(2026, 9, 29, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Every_trigger_is_named_after_the_event_it_is_for_and_named_once()
    {
        foreach (var trigger in Triggers.All)
        {
            Assert.Equal(trigger.Event.Name, trigger.Name);
            Assert.True(typeof(IDomainEvent).IsAssignableFrom(trigger.Event));
        }

        Assert.Equal(Triggers.All.Count, Triggers.All.Select(one => one.Name).Distinct().Count());
    }

    [Fact]
    public void Every_trigger_summary_link_and_reference_field_is_one_its_event_carries()
    {
        foreach (var trigger in Triggers.All)
        {
            Assert.Null(Wording.Refusal(trigger.Summary, trigger));
            Assert.Null(Wording.Refusal(trigger.Link, trigger));

            foreach (var person in trigger.People)
            {
                Assert.Equal(FieldKind.Reference, trigger.Field(person)?.Kind);
            }

            if (trigger.Project is { } project)
            {
                Assert.Equal(FieldKind.Reference, trigger.Field(project)?.Kind);
            }

            if (trigger.Department is { } department)
            {
                Assert.Equal(FieldKind.Reference, trigger.Field(department)?.Kind);
            }
        }
    }

    /// <summary>
    /// What a rule may never start from. Each carries something that should not flow into a
    /// work item title, a notice or somebody else's webhook — pay, or where a person signed in.
    /// </summary>
    [Theory]
    [InlineData("EmployeeTermsChanged")]
    [InlineData("SignedInSomewhereNew")]
    [InlineData("PayRunApproved")]
    [InlineData("AutomationRunDue")]
    public void Some_events_are_not_offered_to_rules(string name) =>
        Assert.Null(Triggers.Find(name));

    [Fact]
    public void Every_template_is_written_in_what_its_trigger_carries()
    {
        foreach (var template in Templates.All)
        {
            var trigger = Triggers.Required(template.Trigger);
            var rule = template.Build(At);

            Assert.NotEmpty(rule.Actions);
            Assert.False(rule.IsOn);
            Assert.True(rule.IsTemplate);

            foreach (var condition in rule.Conditions)
            {
                Assert.Null(Conditions.Refusal(trigger, condition.Field, condition.Operator, condition.Value));
            }

            foreach (var action in rule.Actions)
            {
                Assert.Null(Wording.Refusal(action.Text, trigger));
                Assert.Null(Wording.Refusal(action.Body, trigger));

                var roles = action.Kind != AutomationActionKind.RaiseWork;
                var required = action.Kind is AutomationActionKind.Notify or AutomationActionKind.Email
                    or AutomationActionKind.OnboardingSteps;

                Assert.Null(Who.Refusal(trigger, action.Who, roles, required));

                if (Who.Parse(action.Where) is (WhoKind.Field, var field))
                {
                    Assert.Equal(trigger.Project, field);
                }
            }
        }
    }

    [Fact]
    public void The_templates_cover_the_three_automations_the_brief_names_and_its_own_example()
    {
        var triggers = Templates.All.Select(one => one.Trigger).ToHashSet();

        Assert.Contains("ProjectStarted", triggers);
        Assert.Contains("EmployeeHired", triggers);
        Assert.Contains("ClientTakenOn", triggers);
        Assert.Contains("InvoiceOverdue", triggers);
    }

    /// <summary>
    /// Every trigger reaches the matcher through the outbox: its event is one the registry can
    /// rebuild from a stored row, and the running application has a handler for it. A trigger
    /// listed and not registered would be offered on the screen and never fire.
    /// </summary>
    [Fact]
    public void Every_trigger_has_the_matcher_listening_for_it()
    {
        using var scope = factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<DomainEventRegistry>();

        foreach (var trigger in Triggers.All)
        {
            Assert.Equal(trigger.Event, registry.Find(trigger.Name));

            var handlers = scope.ServiceProvider.GetServices(
                typeof(IDomainEventHandler<>).MakeGenericType(trigger.Event));

            Assert.Contains(handlers, handler => handler!.GetType().Name.StartsWith("MatchRules", StringComparison.Ordinal));
        }

        Assert.Contains(
            scope.ServiceProvider.GetServices<IDomainEventHandler<AutomationRunDue>>(),
            handler => handler.GetType().Name == "CarryOutAutomation");
    }

    /// <summary>
    /// The templates are in the database from the first start, switched off, once each.
    /// </summary>
    [Fact]
    public async Task The_templates_are_there_from_the_first_start_and_seeding_again_adds_nothing()
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<AutomationService>();
        var database = scope.ServiceProvider
            .GetRequiredService<AppDbContext>();

        Assert.Equal(0, await service.SeedTemplatesAsync());

        var seeded = database.AutomationRules.Where(one => one.TemplateKey != null).ToList();

        Assert.Equal(Templates.All.Count, seeded.Count);
        Assert.All(seeded, rule => Assert.False(rule.IsOn));
    }
}
