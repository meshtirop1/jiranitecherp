using JiranisokoTech.Domain.Contracts;
using Money = JiranisokoTech.Domain.Common.Money;

namespace JiranisokoTech.Tests.Domain;

/// <summary>
/// The contract, and the rules that decide whether a bill has anything behind
/// it.
/// </summary>
public class ContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly Today = new(2026, 9, 21);

    private static Contract Drafted() =>
        Contract.Draft(Guid.CreateVersion7(), "JTS-C-2026-001", "Fleet tracking, year one", "KES");

    /// <summary>
    /// A contract in force, running from a month ago to a year from now.
    /// </summary>
    private static Contract InForce(long minorUnits = 1_200_000_00)
    {
        var contract = Drafted();

        contract.WorthUpTo(Money.Of(minorUnits, "KES"));
        contract.Runs(Today.AddMonths(-1), Today.AddYears(1));
        contract.Activate(Now);

        return contract;
    }

    [Fact]
    public void A_contract_starts_as_a_draft_with_nothing_agreed_on_it()
    {
        var contract = Drafted();

        Assert.Equal(ContractState.Draft, contract.State);
        Assert.Null(contract.Value);
        Assert.Null(contract.StartsOn);
        Assert.Null(contract.EndsOn);
        Assert.False(contract.HasTerms);
        Assert.False(contract.CoversOn(Today));
        Assert.Single(contract.Events.OfType<ContractDrafted>());
    }

    /// <summary>The currency is normalised, so the column cannot hold "kes ".</summary>
    [Fact]
    public void The_currency_is_an_iso_code_or_the_contract_is_refused()
    {
        Assert.Equal(
            "KES",
            Contract.Draft(Guid.CreateVersion7(), "JTS-C-1", "Anything", " kes ").Currency);

        Assert.Throws<ArgumentException>(
            () => Contract.Draft(Guid.CreateVersion7(), "JTS-C-2", "Anything", "shillings"));
    }

    /// <summary>
    /// A contract that ends before it starts covers nothing, and is a typo
    /// somebody would otherwise only find when an invoice was refused against
    /// it.
    /// </summary>
    [Fact]
    public void An_end_date_before_the_start_is_refused()
    {
        var contract = Drafted();

        var refused = Assert.Throws<ArgumentException>(
            () => contract.Runs(Today, Today.AddDays(-1)));

        Assert.Contains("before it starts", refused.Message);

        // Neither date is kept, so a refused pair cannot leave half of itself
        // behind for the next attempt to be judged against.
        Assert.Null(contract.StartsOn);
        Assert.Null(contract.EndsOn);
    }

    [Fact]
    public void A_single_day_is_a_span_and_is_allowed()
    {
        var contract = Drafted();

        contract.Runs(Today, Today);

        Assert.Equal(Today, contract.EndsOn);
    }

    [Fact]
    public void A_contract_worth_nothing_is_refused()
    {
        var contract = Drafted();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => contract.WorthUpTo(Money.Of(0, "KES")));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => contract.WorthUpTo(Money.Of(-5_000_00, "KES")));

        Assert.Null(contract.Value);
    }

    /// <summary>
    /// A figure in another currency is refused here rather than by Money's
    /// arithmetic, so the message is about the contract and not about addition.
    /// </summary>
    [Fact]
    public void A_figure_in_another_currency_is_refused_by_name()
    {
        var contract = Drafted();

        var refused = Assert.Throws<InvalidOperationException>(
            () => contract.WorthUpTo(Money.Of(10_000_00, "USD")));

        Assert.Contains("KES", refused.Message);
        Assert.Contains("USD", refused.Message);
    }

    /// <summary>
    /// A contract in force with no figure authorises any amount, and one with no
    /// dates authorises it for ever. Both read on a screen as a contract in good
    /// order.
    /// </summary>
    [Fact]
    public void Bringing_one_into_force_needs_a_value_and_dates()
    {
        var contract = Drafted();

        var noValue = Assert.Throws<InvalidOperationException>(() => contract.Activate(Now));
        Assert.Contains("no value", noValue.Message);

        contract.WorthUpTo(Money.Of(1_200_000_00, "KES"));

        var noDates = Assert.Throws<InvalidOperationException>(() => contract.Activate(Now));
        Assert.Contains("no dates", noDates.Message);

        contract.Runs(Today, Today.AddYears(1));
        contract.Activate(Now);

        Assert.Equal(ContractState.Active, contract.State);
        Assert.Equal(Now, contract.ActivatedAt);
        Assert.True(contract.CoversOn(Today));

        var announced = Assert.Single(contract.Events.OfType<ContractActivated>());

        Assert.Equal(1_200_000_00, announced.MinorUnits);
        Assert.Equal(Today.AddYears(1), announced.EndsOn);
    }

    /// <summary>
    /// Expiry is read from the end date and never written down.
    /// </summary>
    /// <remarks>
    /// The rule this module was most likely to get wrong. A stored Expired state
    /// needs a nightly job to become true, and is wrong for everybody who looks
    /// between midnight and whenever that job runs — so there is no such state,
    /// and a contract past its end date is still recorded as active while
    /// reading as expired. The contract running to next year is here to be not
    /// counted: a check that reported every active contract as expired would
    /// pass without it.
    /// </remarks>
    [Fact]
    public void Expiry_is_a_matter_of_the_date_rather_than_a_state_somebody_sets()
    {
        var over = Drafted();
        over.WorthUpTo(Money.Of(500_000_00, "KES"));
        over.Runs(Today.AddMonths(-6), Today.AddDays(-1));
        over.Activate(Now);

        var running = InForce();

        Assert.Equal(ContractState.Active, over.State);
        Assert.True(over.HasExpiredOn(Today));
        Assert.False(over.CoversOn(Today));

        // The one that must not be counted.
        Assert.False(running.HasExpiredOn(Today));
        Assert.True(running.CoversOn(Today));

        // And it was not expired the day it ended, only the day after.
        Assert.False(over.HasExpiredOn(Today.AddDays(-1)));
    }

    /// <summary>
    /// A contract signed in November to begin in January authorises nothing in
    /// December.
    /// </summary>
    [Fact]
    public void A_contract_that_has_not_started_yet_covers_nothing()
    {
        var contract = Drafted();

        contract.WorthUpTo(Money.Of(300_000_00, "KES"));
        contract.Runs(Today.AddMonths(1), Today.AddMonths(13));
        contract.Activate(Now);

        Assert.False(contract.CoversOn(Today));
        Assert.False(contract.HasExpiredOn(Today));
        Assert.True(contract.CoversOn(Today.AddMonths(1)));
    }

    /// <summary>
    /// The client is holding a signed copy. Editing ours leaves the two
    /// disagreeing with nothing to say which is right.
    /// </summary>
    [Fact]
    public void The_terms_of_a_contract_in_force_cannot_be_rewritten()
    {
        var contract = InForce();

        var value = Assert.Throws<InvalidOperationException>(
            () => contract.WorthUpTo(Money.Of(9_000_000_00, "KES")));

        var dates = Assert.Throws<InvalidOperationException>(
            () => contract.Runs(Today, Today.AddYears(5)));

        Assert.Contains("active", value.Message);
        Assert.Contains("draft", value.Message);
        Assert.Contains("active", dates.Message);

        Assert.Equal(Money.Of(1_200_000_00, "KES"), contract.Value);
    }

    [Fact]
    public void An_extension_only_ever_moves_the_end_later()
    {
        var contract = InForce();
        var agreed = contract.EndsOn!.Value;

        var backwards = Assert.Throws<ArgumentException>(
            () => contract.Extend(agreed.AddDays(-1), Now));

        Assert.Contains("already runs to", backwards.Message);

        // The same date is not an extension either.
        Assert.Throws<ArgumentException>(() => contract.Extend(agreed, Now));

        contract.Extend(agreed.AddMonths(6), Now);

        Assert.Equal(agreed.AddMonths(6), contract.EndsOn);
        Assert.Single(contract.Events.OfType<ContractExtended>());
    }

    /// <summary>
    /// Extensions are agreed late all the time, so one may be recorded after the
    /// contract has already run out. Refusing it would leave the only honest
    /// record of the arrangement outside this system.
    /// </summary>
    [Fact]
    public void A_contract_that_has_already_run_out_can_still_be_extended()
    {
        var contract = Drafted();
        contract.WorthUpTo(Money.Of(400_000_00, "KES"));
        contract.Runs(Today.AddMonths(-6), Today.AddDays(-14));
        contract.Activate(Now);

        contract.Extend(Today.AddMonths(6), Now);

        Assert.False(contract.HasExpiredOn(Today));
        Assert.True(contract.CoversOn(Today));
    }

    [Fact]
    public void A_draft_cannot_be_extended_because_it_does_not_run_yet()
    {
        var contract = Drafted();

        var refused = Assert.Throws<InvalidOperationException>(
            () => contract.Extend(Today.AddYears(2), Now));

        Assert.Contains("draft", refused.Message);
        Assert.Contains("active", refused.Message);
    }

    /// <summary>
    /// A terminated contract is the answer to "why did we stop billing them",
    /// and the person who asks that is never the person who ended it.
    /// </summary>
    [Fact]
    public void Terminating_needs_a_reason_and_keeps_it()
    {
        var contract = InForce();

        Assert.Throws<ArgumentException>(() => contract.Terminate("   ", Now));
        Assert.Equal(ContractState.Active, contract.State);

        contract.Terminate("They took delivery in house.", Now.AddDays(30));

        Assert.Equal(ContractState.Terminated, contract.State);
        Assert.Equal(Now.AddDays(30), contract.EndedAt);
        Assert.Equal("They took delivery in house.", contract.Outcome);
        Assert.False(contract.CoversOn(Today));

        // Terminated, so not expired: the calendar is no longer what ended it.
        Assert.False(contract.HasExpiredOn(Today.AddYears(2)));

        var announced = Assert.Single(contract.Events.OfType<ContractTerminated>());

        Assert.Equal(ContractState.Active, announced.From);
    }

    /// <summary>
    /// Terminating twice is refused rather than ignored, and the refusal repeats
    /// the reason recorded the first time.
    /// </summary>
    /// <remarks>
    /// Ignoring it would quietly discard a second, different reason — which is
    /// the one somebody was trying to correct the record with.
    /// </remarks>
    [Fact]
    public void A_terminated_contract_cannot_be_terminated_again()
    {
        var contract = InForce();

        contract.Terminate("They took delivery in house.", Now);

        var refused = Assert.Throws<InvalidOperationException>(
            () => contract.Terminate("Something else entirely.", Now));

        Assert.Contains("already terminated", refused.Message);
        Assert.Contains("They took delivery in house.", refused.Message);
        Assert.Equal("They took delivery in house.", contract.Outcome);
    }

    [Fact]
    public void A_terminated_contract_cannot_be_brought_back_into_force()
    {
        var contract = InForce();

        contract.Terminate("Ended by agreement.", Now);

        var refused = Assert.Throws<InvalidOperationException>(() => contract.Activate(Now));

        Assert.Contains("terminated", refused.Message);
        Assert.Contains("active", refused.Message);
        Assert.Equal(ContractState.Terminated, contract.State);
    }

    /// <summary>
    /// A negotiation that came to nothing is closed the same way, which is the
    /// only thing that stops abandoned drafts piling up indistinguishable from
    /// the ones still being argued over.
    /// </summary>
    [Fact]
    public void A_draft_can_be_terminated_so_a_dead_negotiation_can_be_closed()
    {
        var contract = Drafted();

        contract.Terminate("They went with somebody else.", Now);

        Assert.Equal(ContractState.Terminated, contract.State);
        Assert.Equal(
            ContractState.Draft,
            Assert.Single(contract.Events.OfType<ContractTerminated>()).From);
    }

    /// <summary>
    /// Bringing the same contract into force twice is a double submit, not a
    /// mistake worth refusing.
    /// </summary>
    [Fact]
    public void Activating_one_already_in_force_changes_nothing()
    {
        var contract = InForce();
        var signed = contract.ActivatedAt;

        contract.Activate(Now.AddDays(5));

        Assert.Equal(signed, contract.ActivatedAt);
        Assert.Single(contract.Events.OfType<ContractActivated>());
    }

    /// <summary>
    /// The title is how this firm finds it in a list. The reference is what both
    /// sides quote, and is fixed.
    /// </summary>
    [Fact]
    public void Retitling_leaves_the_reference_alone()
    {
        var contract = InForce();

        contract.Retitle("  Fleet tracking, extended  ");

        Assert.Equal("Fleet tracking, extended", contract.Title);
        Assert.Equal("JTS-C-2026-001", contract.Reference);
    }

    [Fact]
    public void A_contract_needs_a_reference_and_a_title()
    {
        Assert.Throws<ArgumentException>(
            () => Contract.Draft(Guid.CreateVersion7(), " ", "Fleet tracking", "KES"));

        Assert.Throws<ArgumentException>(
            () => Contract.Draft(Guid.CreateVersion7(), "JTS-C-3", "  ", "KES"));
    }
}
