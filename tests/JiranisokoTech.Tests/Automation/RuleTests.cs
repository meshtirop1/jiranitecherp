using JiranisokoTech.Application.Automation;
using JiranisokoTech.Domain.Automation;
using JiranisokoTech.Domain.Clients;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Domain.Money;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Renewals;
using JiranisokoTech.Domain.Work;

namespace JiranisokoTech.Tests.Automation;

/// <summary>
/// The rule and the run on their own, and the IF of section 31: every comparison a condition
/// can make, and every one it must refuse to be written with.
/// </summary>
public class RuleTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 29, 9, 0, 0, TimeSpan.Zero);

    private static Trigger Hired => Triggers.Required(nameof(EmployeeHired));

    [Fact]
    public void A_new_rule_is_off_and_cannot_be_switched_on_with_nothing_to_do()
    {
        var rule = AutomationRule.Write("Welcome", nameof(ClientTakenOn), null, At);

        Assert.False(rule.IsOn);

        var refusal = Assert.Throws<InvalidOperationException>(() => rule.SwitchOn(At));
        Assert.Contains("something to do", refusal.Message);

        rule.Then(AutomationActionKind.Notify, "Hello", who: "role:sales");
        rule.SwitchOn(At);

        Assert.True(rule.IsOn);
        Assert.Equal(At, rule.SwitchedOnAt);
    }

    /// <summary>
    /// Taking away the last action switches the rule off, rather than leaving it on and doing
    /// nothing for every event — a history full of runs that did nothing looks like work.
    /// </summary>
    [Fact]
    public void Removing_the_last_action_switches_the_rule_off()
    {
        var rule = AutomationRule.Write("Welcome", nameof(ClientTakenOn), null, At);
        var only = rule.Then(AutomationActionKind.Notify, "Hello", who: "role:sales");
        rule.SwitchOn(At);

        rule.DropAction(only.Id);

        Assert.False(rule.IsOn);
        Assert.Empty(rule.Actions);
    }

    [Fact]
    public void A_rule_does_at_most_ten_things_and_tests_at_most_ten()
    {
        var rule = AutomationRule.Write("Busy", nameof(ClientTakenOn), null, At);

        for (var i = 0; i < AutomationRule.MostActions; i++)
        {
            rule.Then(AutomationActionKind.Notify, $"Line {i}", who: "role:sales");
            rule.When(nameof(ClientTakenOn.Name), ConditionOperator.IsSet, null);
        }

        Assert.Throws<InvalidOperationException>(
            () => rule.Then(AutomationActionKind.Notify, "One more", who: "role:sales"));
        Assert.Throws<InvalidOperationException>(
            () => rule.When(nameof(ClientTakenOn.Name), ConditionOperator.IsSet, null));
    }

    [Fact]
    public void Actions_keep_the_order_they_were_added_in_after_one_is_removed()
    {
        var rule = AutomationRule.Write("Ordered", nameof(ClientTakenOn), null, At);
        var first = rule.Then(AutomationActionKind.Notify, "First", who: "role:sales");
        rule.Then(AutomationActionKind.Notify, "Second", who: "role:sales");

        rule.DropAction(first.Id);
        rule.Then(AutomationActionKind.Notify, "Third", who: "role:sales");

        Assert.Equal(["Second", "Third"], rule.Actions.Select(one => one.Text));
    }

    [Fact]
    public void A_delay_is_between_nothing_and_thirty_days()
    {
        var rule = AutomationRule.Write("Later", nameof(ClientTakenOn), null, At);

        rule.Describe("Later", null, AutomationRule.MostDelayMinutes);
        Assert.Equal(AutomationRule.MostDelayMinutes, rule.DelayMinutes);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => rule.Describe("Later", null, AutomationRule.MostDelayMinutes + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => rule.Describe("Later", null, -1));
    }

    [Fact]
    public void A_rule_with_no_delay_is_queued_at_once_and_one_with_a_delay_waits()
    {
        var now = AutomationRule.Write("Now", nameof(ClientTakenOn), null, At);
        var later = AutomationRule.Write("Later", nameof(ClientTakenOn), null, At);
        later.Describe("Later", null, 60);

        var immediate = AutomationRun.Matched(now, Guid.NewGuid(), "{}", "Acme", [], At);
        var waiting = AutomationRun.Matched(later, Guid.NewGuid(), "{}", "Acme", [], At);

        Assert.Equal(AutomationRunStatus.Queued, immediate.Status);
        Assert.Single(immediate.Events.OfType<AutomationRunDue>());

        Assert.Equal(AutomationRunStatus.Waiting, waiting.Status);
        Assert.Equal(At.AddMinutes(60), waiting.DueAt);
        Assert.Empty(waiting.Events);

        waiting.Queue(At.AddMinutes(61));

        Assert.Equal(AutomationRunStatus.Queued, waiting.Status);
        Assert.Single(waiting.Events.OfType<AutomationRunDue>());
    }

    /// <summary>
    /// A run's failures are counted the way the outbox counts them, so the history says "gave
    /// up" on the attempt the outbox abandons the message.
    /// </summary>
    [Fact]
    public void A_run_gives_up_on_the_attempt_the_outbox_does()
    {
        var rule = AutomationRule.Write("Flaky", nameof(ClientTakenOn), null, At);
        var run = AutomationRun.Matched(rule, Guid.NewGuid(), "{}", "Acme", [], At);

        run.Failed("mail server down", giveUpAfter: 3);
        run.Failed("mail server down", giveUpAfter: 3);
        Assert.Equal(AutomationRunStatus.Retrying, run.Status);

        run.Failed("mail server down", giveUpAfter: 3);
        Assert.Equal(AutomationRunStatus.GaveUp, run.Status);
        Assert.Equal(3, run.Attempts);
    }

    [Fact]
    public void A_chain_is_written_and_read_back_in_order()
    {
        Guid[] chain = [Guid.CreateVersion7(), Guid.CreateVersion7()];

        Assert.Equal(chain, AutomationRun.Parse(AutomationRun.Stamp(chain)));
        Assert.Null(AutomationRun.Stamp([]));
        Assert.Empty(AutomationRun.Parse(null));
        Assert.Empty(AutomationRun.Parse("not,ids"));
    }

    // --- conditions ---------------------------------------------------------------------

    [Theory]
    [InlineData(ConditionOperator.Is, "Wanjiru Kamau", true)]
    [InlineData(ConditionOperator.Is, "wanjiru kamau", true)]
    [InlineData(ConditionOperator.IsNot, "Otieno", true)]
    [InlineData(ConditionOperator.Contains, "kamau", true)]
    [InlineData(ConditionOperator.DoesNotContain, "kamau", false)]
    [InlineData(ConditionOperator.IsSet, null, true)]
    [InlineData(ConditionOperator.IsNotSet, null, false)]
    public void Text_is_compared_without_regard_to_case(
        ConditionOperator comparison, string? value, bool holds) =>
        Assert.Equal(holds, Holds(nameof(EmployeeHired.FullName), comparison, value));

    [Theory]
    [InlineData(ConditionOperator.Is, "2026-10-05", true)]
    [InlineData(ConditionOperator.AtLeast, "2026-10-01", true)]
    [InlineData(ConditionOperator.AtMost, "2026-10-01", false)]
    [InlineData(ConditionOperator.IsNot, "2026-10-05", false)]
    public void Dates_are_compared_as_dates(ConditionOperator comparison, string value, bool holds) =>
        Assert.Equal(holds, Holds(nameof(EmployeeHired.StartsOn), comparison, value));

    [Theory]
    [InlineData(ConditionOperator.AtLeast, "7", true)]
    [InlineData(ConditionOperator.AtLeast, "11", false)]
    [InlineData(ConditionOperator.AtMost, "10", true)]
    [InlineData(ConditionOperator.Is, "10", true)]
    [InlineData(ConditionOperator.Is, "10.0", true)]
    public void Numbers_are_compared_as_numbers(ConditionOperator comparison, string value, bool holds)
    {
        var overdue = Triggers.Required(nameof(InvoiceOverdue));
        var facts = Facts.Of(overdue, new InvoiceOverdue(
            Guid.NewGuid(), Guid.NewGuid(), "INV-7", new DateOnly(2026, 9, 19), 10,
            ReminderStage.First, 250_000, "KES"));

        Assert.Equal(holds, Holds(overdue, facts, nameof(InvoiceOverdue.DaysOverdue), comparison, value));
    }

    [Theory]
    [InlineData(ConditionOperator.Is, "Done", true)]
    [InlineData(ConditionOperator.Is, "done", true)]
    [InlineData(ConditionOperator.IsNot, "Done", false)]
    [InlineData(ConditionOperator.Is, "InReview", false)]
    public void A_choice_is_compared_by_name(ConditionOperator comparison, string value, bool holds)
    {
        var moved = Triggers.Required(nameof(WorkItemMoved));
        var facts = Facts.Of(moved, new WorkItemMoved(
            Guid.NewGuid(), WorkItemStatus.InProgress, WorkItemStatus.Done, null));

        Assert.Equal(holds, Holds(moved, facts, nameof(WorkItemMoved.To), comparison, value));
    }

    /// <summary>
    /// An empty field is not equal to anything, and is "not" everything — so a department
    /// condition on somebody hired into no department reads the way a person would expect.
    /// </summary>
    [Fact]
    public void A_field_with_nothing_in_it_is_only_ever_not_something()
    {
        var department = Guid.NewGuid().ToString();

        Assert.False(Holds(nameof(EmployeeHired.DepartmentId), ConditionOperator.Is, department));
        Assert.True(Holds(nameof(EmployeeHired.DepartmentId), ConditionOperator.IsNot, department));
        Assert.True(Holds(nameof(EmployeeHired.DepartmentId), ConditionOperator.IsNotSet, null));
    }

    [Fact]
    public void All_conditions_must_hold()
    {
        var rule = AutomationRule.Write("Both", nameof(EmployeeHired), null, At);
        rule.When(nameof(EmployeeHired.FullName), ConditionOperator.Contains, "Kamau");
        rule.When(nameof(EmployeeHired.StartsOn), ConditionOperator.AtMost, "2026-10-01");

        Assert.False(Conditions.AllHold(rule.Conditions, Hiring()));

        rule.DropCondition(rule.Conditions[1].Id);

        Assert.True(Conditions.AllHold(rule.Conditions, Hiring()));
    }

    [Theory]
    [InlineData("Nonsense", ConditionOperator.Is, "x", "is not something")]
    [InlineData(nameof(EmployeeHired.StartsOn), ConditionOperator.Contains, "2026", "cannot be compared that way")]
    [InlineData(nameof(EmployeeHired.StartsOn), ConditionOperator.Is, "5 October", "Write it as 2026-10-01")]
    [InlineData(nameof(EmployeeHired.FullName), ConditionOperator.Is, "", "Say what")]
    [InlineData(nameof(EmployeeHired.EmployeeId), ConditionOperator.Is, "Wanjiru", "identifier")]
    public void A_condition_that_could_never_be_read_is_refused_when_written(
        string field, ConditionOperator comparison, string value, string said)
    {
        var refusal = Conditions.Refusal(Hired, field, comparison, value);

        Assert.NotNull(refusal);
        Assert.Contains(said, refusal);
    }

    [Fact]
    public void A_choice_outside_the_list_is_refused_with_the_list()
    {
        var refusal = Conditions.Refusal(
            Triggers.Required(nameof(WorkItemMoved)), nameof(WorkItemMoved.To),
            ConditionOperator.Is, "Finished");

        Assert.NotNull(refusal);
        Assert.Contains("Done", refusal);
    }

    [Fact]
    public void A_condition_about_when_something_happened_is_not_offered()
    {
        var sent = Triggers.Required(nameof(InvoiceSent));

        Assert.DoesNotContain(sent.Testable, field => field.Name == nameof(InvoiceSent.At));
        Assert.NotNull(Conditions.Refusal(sent, nameof(InvoiceSent.At), ConditionOperator.IsSet, null));
    }

    // --- wording ------------------------------------------------------------------------

    [Fact]
    public void Text_is_filled_from_the_event_and_the_rule()
    {
        var said = Wording.Fill("Accounts for {FullName} from {StartsOn} ({Rule})", Hiring(), "Joiners");

        Assert.Equal("Accounts for Wanjiru Kamau from 5 Oct 2026 (Joiners)", said);
    }

    [Fact]
    public void A_name_the_event_does_not_carry_is_refused_and_the_ones_it_does_are_listed()
    {
        var refusal = Wording.Refusal("Welcome {FulName}", Hired);

        Assert.NotNull(refusal);
        Assert.Contains("{FulName}", refusal);
        Assert.Contains("{FullName}", refusal);
        Assert.Null(Wording.Refusal("Welcome {FullName}, says {Rule}", Hired));
    }

    [Fact]
    public void A_link_is_left_out_when_the_field_it_needs_is_empty()
    {
        var merged = Triggers.Required(nameof(PullRequestMerged));
        var named = Guid.NewGuid();

        var none = Facts.Of(merged, new PullRequestMerged(
            Guid.NewGuid(), Guid.NewGuid(), 4, "main", null, At));
        var some = Facts.Of(merged, new PullRequestMerged(
            Guid.NewGuid(), Guid.NewGuid(), 4, "main", named, At));

        Assert.Null(Wording.Link(none));
        Assert.Equal($"/work/{named}", Wording.Link(some));
    }

    [Fact]
    public void Money_fields_say_that_they_are_in_cents()
    {
        var overdue = Triggers.Required(nameof(InvoiceOverdue));

        Assert.Equal("Outstanding amount (in cents)",
            overdue.Field(nameof(InvoiceOverdue.OutstandingMinorUnits))!.Label);
        Assert.Equal("Days overdue", overdue.Field(nameof(InvoiceOverdue.DaysOverdue))!.Label);
    }

    // --- who ------------------------------------------------------------------------------

    [Fact]
    public void Work_is_given_to_a_person_and_never_to_a_role()
    {
        Assert.NotNull(Who.Refusal(Hired, "role:hr", roles: false, required: false));
        Assert.Null(Who.Refusal(Hired, "role:hr", roles: true, required: true));
        Assert.Null(Who.Refusal(Hired, "manager:EmployeeId", roles: false, required: false));
    }

    [Fact]
    public void A_who_names_only_what_the_trigger_carries()
    {
        Assert.NotNull(Who.Refusal(Hired, "field:ProjectId", roles: true, required: true));
        Assert.NotNull(Who.Refusal(Hired, "lead:ProjectId", roles: true, required: true));
        Assert.NotNull(Who.Refusal(Hired, "role:emperor", roles: true, required: true));
        Assert.NotNull(Who.Refusal(Hired, null, roles: true, required: true));
        Assert.Null(Who.Refusal(Hired, "head:DepartmentId", roles: true, required: true));
    }

    private static Facts Hiring() =>
        Facts.Of(Hired, new EmployeeHired(Guid.NewGuid(), "Wanjiru Kamau", null, new DateOnly(2026, 10, 5)));

    private static bool Holds(string field, ConditionOperator comparison, string? value) =>
        Holds(Hired, Hiring(), field, comparison, value);

    private static bool Holds(
        Trigger trigger, Facts facts, string field, ConditionOperator comparison, string? value)
    {
        Assert.Null(Conditions.Refusal(trigger, field, comparison, value));

        var rule = AutomationRule.Write("Test", trigger.Name, null, At);
        var condition = rule.When(field, comparison, value);

        return Conditions.Holds(condition, facts);
    }
}
