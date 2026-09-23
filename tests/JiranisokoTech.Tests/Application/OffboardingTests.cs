using JiranisokoTech.Domain.People;

namespace JiranisokoTech.Tests.Application;

/// <summary>
/// What still has to happen when somebody leaves.
/// </summary>
/// <remarks>
/// Section 9 released a leaver's open work and stopped there. The laptop, the building
/// pass and the sign-in survived the departure indefinitely, and nothing anywhere said
/// so — which is never a decision, only an omission.
///
/// The most important test here is the one that refuses to close a departure with the
/// access still live, because the item usually left is exactly that one.
/// </remarks>
public class OffboardingTests
{
    private static readonly DateOnly Leaving = new(2026, 10, 31);

    private static readonly DateTimeOffset Now =
        new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static Offboarding Begun() =>
        Offboarding.Begin(Guid.CreateVersion7(), Leaving, Now);

    /// <summary>
    /// A departure cannot be closed while the sign-in is still live.
    /// </summary>
    /// <remarks>
    /// The one rule in this feature with teeth. A checklist that can be closed with items
    /// on it is a checklist that gets closed with items on it, and the item left is
    /// usually the one whose omission is a security finding rather than an inconvenience.
    /// </remarks>
    [Fact]
    public void A_departure_cannot_be_closed_with_the_sign_in_still_live()
    {
        var leaving = Begun();

        var refused = Assert.Throws<InvalidOperationException>(() => leaving.Complete(Now));

        Assert.Contains("sign-in", refused.Message);
        Assert.False(leaving.IsComplete);
    }

    /// <summary>A departure cannot be closed while the firm's property is still out.</summary>
    [Fact]
    public void A_departure_cannot_be_closed_with_a_laptop_still_out()
    {
        var leaving = Begun();

        leaving.AccessRemoved(Guid.CreateVersion7(), Now);
        leaving.Lent(AssetKind.Laptop, "MacBook Pro 14, 2025", "C02X1234");

        var refused = Assert.Throws<InvalidOperationException>(() => leaving.Complete(Now));

        Assert.Contains("MacBook", refused.Message);
    }

    [Fact]
    public void A_departure_with_everything_dealt_with_can_be_closed()
    {
        var leaving = Begun();

        leaving.AccessRemoved(Guid.CreateVersion7(), Now);
        leaving.Lent(AssetKind.Laptop, "MacBook Pro 14, 2025", "C02X1234");

        var laptop = Assert.Single(leaving.Assets);
        leaving.Returned(laptop.Id, Leaving, "Screen scratched");

        leaving.Complete(Now);

        Assert.True(leaving.IsComplete);
        Assert.False(leaving.HasOutstandingItems);
    }

    /// <summary>
    /// Something that was never lent can be taken off the list.
    /// </summary>
    /// <remarks>
    /// Because the realistic mistake is adding an item that was never lent, and a
    /// checklist with a phantom item on it can never be finished — so somebody would
    /// mark it returned instead, which puts a falsehood in the record to unblock a
    /// button.
    /// </remarks>
    [Fact]
    public void Something_never_lent_can_be_removed_from_the_list()
    {
        var leaving = Begun();

        leaving.AccessRemoved(Guid.CreateVersion7(), Now);
        leaving.Lent(AssetKind.Phone, "iPhone 17", null);

        leaving.Forget(Assert.Single(leaving.Assets).Id);

        Assert.Empty(leaving.Assets);
        leaving.Complete(Now);

        Assert.True(leaving.IsComplete);
    }

    /// <summary>
    /// An exit interview is not required to close a departure.
    /// </summary>
    /// <remarks>
    /// Somebody may decline one. A system that insisted would get "declined" typed into a
    /// notes field to make a button work, which is worse than an empty field — it looks
    /// like a conversation that happened.
    /// </remarks>
    [Fact]
    public void An_exit_interview_is_not_required_to_close_a_departure()
    {
        var leaving = Begun();

        leaving.AccessRemoved(Guid.CreateVersion7(), Now);
        leaving.Complete(Now);

        Assert.True(leaving.IsComplete);
        Assert.Null(leaving.ExitInterviewAt);
    }

    /// <summary>
    /// Closing the access says who did it.
    /// </summary>
    /// <remarks>
    /// This is the item an auditor asks about, and "somebody dealt with it" is not an
    /// answer. Recorded once and not overwritten, so a second click does not rewrite who
    /// the firm says was responsible.
    /// </remarks>
    [Fact]
    public void Closing_the_sign_in_records_who_and_is_not_overwritten()
    {
        var leaving = Begun();
        var first = Guid.CreateVersion7();

        leaving.AccessRemoved(first, Now);
        leaving.AccessRemoved(Guid.CreateVersion7(), Now.AddHours(1));

        Assert.Equal(first, leaving.AccessRemovedById);
        Assert.Equal(Now, leaving.AccessRemovedAt);
    }

    /// <summary>
    /// The interview notes are kept out of the audit trail.
    /// </summary>
    /// <remarks>
    /// An exit interview is where somebody says what they could not say while employed.
    /// Its whole value depends on not being read by the person they were describing, and
    /// the trail is read by everybody holding audit.view.
    /// </remarks>
    [Fact]
    public void The_interview_notes_never_reach_the_audit_trail() =>
        Assert.Contains(nameof(Offboarding.ExitInterviewNotes), Offboarding.AuditExcludes);

    [Fact]
    public void An_asset_with_no_description_is_refused() =>
        Assert.Throws<ArgumentException>(() => Begun().Lent(AssetKind.Laptop, "  ", null));

    /// <summary>Closing the access is announced, so anything watching can act.</summary>
    /// <remarks>
    /// An event rather than a log line, because removing somebody from the other systems
    /// they had accounts on is the obvious next thing and it has to hear this said.
    /// </remarks>
    [Fact]
    public void Closing_the_sign_in_is_announced()
    {
        var leaving = Begun();
        leaving.ClearEvents();

        leaving.AccessRemoved(Guid.CreateVersion7(), Now);

        Assert.IsType<LeaverAccessRemoved>(Assert.Single(leaving.Events));
    }
}
