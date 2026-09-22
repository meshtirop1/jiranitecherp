using JiranisokoTech.Domain.Work;

namespace JiranisokoTech.Tests.Domain;

public class ProjectTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    private static Project Begun(string name = "Delivery note printer") => Project.Begin(name);

    [Fact]
    public void A_project_begins_planned_with_a_code_people_can_type()
    {
        var project = Project.Begin("Delivery Note Printer");

        Assert.Equal("delivery-note-printer", project.Code);
        Assert.Equal(ProjectStatus.Planned, project.Status);
        Assert.True(project.IsRunning);
        Assert.Single(project.Events.OfType<ProjectStarted>());
    }

    [Fact]
    public void A_code_can_be_given_rather_than_derived()
    {
        Assert.Equal("dnp", Project.Begin("Delivery Note Printer", code: "DNP").Code);
    }

    /// <summary>
    /// The code ends up in addresses, commit messages and conversation, so a
    /// rename must not break every reference already made to it.
    /// </summary>
    [Fact]
    public void Renaming_leaves_the_code_alone()
    {
        var project = Begun();

        project.Rename("Delivery notes, phase two");

        Assert.Equal("delivery-note-printer", project.Code);
    }

    [Fact]
    public void Activating_and_delivering_are_announced()
    {
        var project = Begun();

        project.Activate(Now);
        project.Deliver(Now.AddDays(30));

        Assert.Equal(ProjectStatus.Delivered, project.Status);
        Assert.Equal(Now.AddDays(30), project.DeliveredAt);
        Assert.False(project.IsRunning);
        Assert.Equal(2, project.Events.OfType<ProjectStatusChanged>().Count());
    }

    [Fact]
    public void A_delivered_project_cannot_be_started_again()
    {
        var project = Begun();
        project.Activate(Now);
        project.Deliver(Now);

        Assert.Throws<InvalidOperationException>(() => project.Activate(Now));
        Assert.Throws<InvalidOperationException>(() => project.Deliver(Now));
    }

    /// <summary>
    /// A project on hold with no reason is one nobody can restart, because
    /// nobody remembers what it is waiting for.
    /// </summary>
    [Fact]
    public void Pausing_without_saying_why_is_refused()
    {
        var project = Begun();
        project.Activate(Now);

        Assert.Throws<ArgumentException>(() => project.Hold("  ", Now));
        Assert.Equal(ProjectStatus.Active, project.Status);
    }

    [Fact]
    public void A_paused_project_carries_its_reason_and_can_be_restarted()
    {
        var project = Begun();
        project.Activate(Now);
        project.ClearEvents();

        project.Hold("client budget approval", Now);

        var paused = Assert.Single(project.Events.OfType<ProjectStatusChanged>());
        Assert.Equal("client budget approval", paused.Because);
        Assert.True(project.IsRunning);

        project.Activate(Now.AddDays(7));

        Assert.Equal(ProjectStatus.Active, project.Status);
    }

    [Fact]
    public void Cancelling_needs_a_reason_and_is_final()
    {
        var project = Begun();
        project.Activate(Now);

        Assert.Throws<ArgumentException>(() => project.Cancel(" ", Now));

        project.Cancel("client withdrew", Now);

        Assert.Equal(ProjectStatus.Cancelled, project.Status);
        Assert.Throws<InvalidOperationException>(() => project.Activate(Now));
    }

    [Fact]
    public void Changing_the_lead_says_who_it_was()
    {
        var project = Begun();
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        project.LeadBy(first);
        project.LeadBy(second);

        var changes = project.Events.OfType<ProjectLeadChanged>().ToList();

        Assert.Equal(2, changes.Count);
        Assert.Equal(first, changes[1].FromEmployeeId);
        Assert.Equal(second, changes[1].ToEmployeeId);
    }

    [Fact]
    public void Naming_the_same_lead_again_is_not_a_change()
    {
        var project = Begun();
        var lead = Guid.CreateVersion7();

        project.LeadBy(lead);
        project.ClearEvents();
        project.LeadBy(lead);

        Assert.Empty(project.Events);
    }

    [Fact]
    public void A_name_is_required()
    {
        Assert.Throws<ArgumentException>(() => Project.Begin("   "));
    }
}
