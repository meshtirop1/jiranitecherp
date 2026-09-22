using JiranisokoTech.Domain.Approvals;

namespace JiranisokoTech.Tests.Domain;

/// <summary>
/// The chain, and the state it must never be able to reach.
/// </summary>
/// <remarks>
/// The system this replaces shipped an approval that sat at Pending for ever
/// with no button anywhere that could finish it: every step had been passed
/// over, so nothing was waiting and nothing was decided. Nothing was wrong with
/// any single step. Half the tests here exist to keep that state unreachable.
/// </remarks>
public class ApprovalRequestTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    private readonly Guid _asker = Guid.CreateVersion7();
    private readonly Guid _lead = Guid.CreateVersion7();
    private readonly Guid _head = Guid.CreateVersion7();
    private readonly Guid _director = Guid.CreateVersion7();

    private ApprovalRequest Chain(params Guid[] deciders) =>
        ApprovalRequest.Open(
            "JobRequisition",
            Guid.CreateVersion7(),
            "requisition.open",
            _asker,
            Now,
            deciders.Length > 0 ? deciders : [_lead, _head, _director]);

    [Fact]
    public void A_new_chain_waits_on_its_first_step()
    {
        var chain = Chain();

        Assert.Equal(ApprovalStatus.Pending, chain.Status);
        Assert.Equal(3, chain.Steps.Count);
        Assert.Equal(_lead, chain.WaitingOn);
        Assert.Equal(1, chain.CurrentStep!.Order);
        Assert.Single(chain.Events.OfType<ApprovalRequested>());
    }

    [Fact]
    public void A_chain_with_nobody_on_it_is_refused()
    {
        var refused = Assert.Throws<ArgumentException>(() =>
            ApprovalRequest.Open("JobRequisition", Guid.CreateVersion7(), "x", _asker, Now));

        Assert.Contains("at least one person", refused.Message);
    }

    /// <summary>
    /// The same person twice is one person being asked the same question again,
    /// which nobody does — so the chain would stall on the second ask.
    /// </summary>
    [Fact]
    public void The_same_person_cannot_appear_twice()
    {
        Assert.Throws<ArgumentException>(() => Chain(_lead, _head, _lead));
    }

    [Fact]
    public void Approvals_run_in_order_and_finish_the_chain()
    {
        var chain = Chain();

        chain.Approve(_lead, Now);
        Assert.Equal(_head, chain.WaitingOn);

        chain.Approve(_head, Now);
        Assert.Equal(_director, chain.WaitingOn);

        chain.Approve(_director, Now);

        Assert.Equal(ApprovalStatus.Approved, chain.Status);
        Assert.Null(chain.WaitingOn);
        Assert.Equal(Now, chain.SettledAt);
        Assert.Single(chain.Events.OfType<ApprovalSettled>());
    }

    /// <summary>
    /// Letting a later approver go first reads as efficient and means the head
    /// approves something the lead has not yet seen, which is the opposite of
    /// what a chain is for.
    /// </summary>
    [Fact]
    public void Somebody_further_up_cannot_decide_early()
    {
        var chain = Chain();

        var refused = Assert.Throws<InvalidOperationException>(() => chain.Approve(_head, Now));

        Assert.Contains("step 1 of 3", refused.Message);
        Assert.Equal(_lead, chain.WaitingOn);
    }

    /// <summary>
    /// One no is enough. Carrying on up the chain after a refusal asks somebody
    /// to overrule a colleague without telling them that is what they are doing.
    /// </summary>
    [Fact]
    public void One_refusal_ends_it()
    {
        var chain = Chain();

        chain.Approve(_lead, Now);
        chain.Refuse(_head, Now, "no budget this quarter");

        Assert.Equal(ApprovalStatus.Refused, chain.Status);
        Assert.Equal("no budget this quarter", chain.Outcome);
        Assert.Null(chain.WaitingOn);

        // And the director is never asked.
        Assert.Equal(StepStatus.Waiting, chain.Steps[2].Status);
        Assert.Throws<InvalidOperationException>(() => chain.Approve(_director, Now));
    }

    [Fact]
    public void A_refusal_has_to_say_why()
    {
        var chain = Chain();

        Assert.Throws<ArgumentException>(() => chain.Refuse(_lead, Now, "   "));
        Assert.Equal(ApprovalStatus.Pending, chain.Status);
    }

    [Fact]
    public void A_step_that_cannot_be_decided_can_be_passed_over()
    {
        var chain = Chain();

        chain.Skip(1, Now, "the lead post is vacant");

        Assert.Equal(StepStatus.Skipped, chain.Steps[0].Status);
        Assert.Equal("the lead post is vacant", chain.Steps[0].Note);

        // Nobody decided it, so nobody is recorded as having done so.
        Assert.Null(chain.Steps[0].DecidedById);

        Assert.Equal(_head, chain.WaitingOn);
    }

    /// <summary>
    /// The rule this whole class exists for.
    /// </summary>
    /// <remarks>
    /// Passing over the last waiting step leaves a chain with nothing waiting
    /// and nothing decided, sitting at Pending for ever. That is the exact state
    /// the previous system reached and could not leave.
    /// </remarks>
    [Fact]
    public void The_last_step_left_cannot_be_passed_over()
    {
        var chain = Chain(_lead, _head);

        chain.Skip(1, Now, "the lead post is vacant");

        var refused = Assert.Throws<InvalidOperationException>(
            () => chain.Skip(2, Now, "the head has left too"));

        Assert.Contains("nobody to decide it", refused.Message);
        Assert.Equal(ApprovalStatus.Pending, chain.Status);
        Assert.Equal(_head, chain.WaitingOn);
    }

    [Fact]
    public void A_one_step_chain_cannot_be_passed_over_at_all()
    {
        var chain = Chain(_lead);

        Assert.Throws<InvalidOperationException>(() => chain.Skip(1, Now, "vacant"));
    }

    /// <summary>
    /// The way out of a chain that would otherwise be stuck: the named decider
    /// has gone, and the step goes to whoever took over from them.
    /// </summary>
    [Fact]
    public void A_stuck_step_can_be_handed_to_somebody_else()
    {
        var chain = Chain(_lead);

        chain.Reassign(1, _director, Now);

        Assert.Equal(_director, chain.WaitingOn);

        chain.Approve(_director, Now);

        Assert.Equal(ApprovalStatus.Approved, chain.Status);

        // And the record says who actually decided it.
        Assert.Equal(_director, chain.Steps[0].DecidedById);
    }

    [Fact]
    public void A_step_cannot_be_handed_to_somebody_already_on_the_chain()
    {
        var chain = Chain(_lead, _head);

        Assert.Throws<InvalidOperationException>(() => chain.Reassign(1, _head, Now));
    }

    [Fact]
    public void A_decided_step_cannot_be_handed_on()
    {
        var chain = Chain();
        chain.Approve(_lead, Now);

        Assert.Throws<InvalidOperationException>(() => chain.Reassign(1, _director, Now));
    }

    /// <summary>
    /// A chain finished with a step passed over still finishes. Being unable to
    /// ask somebody is not the same as their having said no.
    /// </summary>
    [Fact]
    public void A_chain_with_a_skipped_step_still_completes()
    {
        var chain = Chain();

        chain.Skip(1, Now, "the lead post is vacant");
        chain.Approve(_head, Now);
        chain.Approve(_director, Now);

        Assert.Equal(ApprovalStatus.Approved, chain.Status);
    }

    [Fact]
    public void Only_the_person_who_asked_can_withdraw_it()
    {
        var chain = Chain();

        Assert.Throws<InvalidOperationException>(() => chain.Withdraw(_head, Now, "changed mind"));

        chain.Withdraw(_asker, Now, "no longer needed");

        Assert.Equal(ApprovalStatus.Withdrawn, chain.Status);
        Assert.Equal("no longer needed", chain.Outcome);
    }

    /// <summary>
    /// Withdrawing after a decision would erase something somebody is
    /// answerable for.
    /// </summary>
    [Fact]
    public void It_cannot_be_withdrawn_once_somebody_has_decided()
    {
        var chain = Chain();
        chain.Approve(_lead, Now);

        var refused = Assert.Throws<InvalidOperationException>(
            () => chain.Withdraw(_asker, Now, "changed mind"));

        Assert.Contains("keeps their decision on the record", refused.Message);
    }

    /// <summary>
    /// A step passed over is not a decision, so it does not block a withdrawal.
    /// </summary>
    [Fact]
    public void A_skipped_step_does_not_block_a_withdrawal()
    {
        var chain = Chain();
        chain.Skip(1, Now, "vacant");

        chain.Withdraw(_asker, Now, "no longer needed");

        Assert.Equal(ApprovalStatus.Withdrawn, chain.Status);
    }

    [Fact]
    public void Nothing_can_be_decided_after_it_is_settled()
    {
        var chain = Chain(_lead);
        chain.Approve(_lead, Now);

        Assert.Throws<InvalidOperationException>(() => chain.Approve(_lead, Now));
        Assert.Throws<InvalidOperationException>(() => chain.Refuse(_lead, Now, "no"));
        Assert.Throws<InvalidOperationException>(() => chain.Skip(1, Now, "vacant"));
        Assert.Throws<InvalidOperationException>(() => chain.Withdraw(_asker, Now, "no"));
    }

    /// <summary>
    /// Every settled chain announces itself. That event is what the requisition,
    /// the leave day or the expense is waiting on, and it is why the approval
    /// engine needs to know nothing about any of them.
    /// </summary>
    [Theory]
    [InlineData(ApprovalStatus.Approved)]
    [InlineData(ApprovalStatus.Refused)]
    [InlineData(ApprovalStatus.Withdrawn)]
    public void Settling_is_announced_however_it_ends(ApprovalStatus expected)
    {
        var chain = Chain(_lead);

        switch (expected)
        {
            case ApprovalStatus.Approved:
                chain.Approve(_lead, Now);
                break;
            case ApprovalStatus.Refused:
                chain.Refuse(_lead, Now, "no");
                break;
            default:
                chain.Withdraw(_asker, Now, "no longer needed");
                break;
        }

        var settled = Assert.Single(chain.Events.OfType<ApprovalSettled>());

        Assert.Equal(expected, settled.Status);
        Assert.Equal("requisition.open", settled.Action);
    }
}
