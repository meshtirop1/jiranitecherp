using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Contracts;

/// <summary>Where a contract stands.</summary>
/// <remarks>
/// Three states, and the fourth one people expect — expired — is deliberately
/// not here. Expiry is a fact about the end date, not an event that happens to
/// the row, and <see cref="Contract.HasExpiredOn"/> answers it from the date the
/// moment somebody asks. A stored Expired would need something to walk the table
/// every night and set it, and would be wrong for everybody who looked between
/// midnight and whenever that job ran — or for ever, on the morning it failed.
/// The same reasoning keeps an invoice from having an Overdue status.
///
/// Terminated stays a state because it is the opposite case: ending a contract
/// early is a decision somebody took on a day, with a reason, and nothing about
/// the dates can be read to discover it.
/// </remarks>
public enum ContractState
{
    /// <summary>Being negotiated. Nothing is agreed and nothing may be billed under it.</summary>
    Draft = 1,

    /// <summary>Signed: what this client may be billed for, and up to how much.</summary>
    Active = 2,

    /// <summary>Ended early by a decision, with a reason.</summary>
    Terminated = 3,
}

/// <summary>
/// What says the firm may bill a client at all, and for how much.
/// </summary>
/// <remarks>
/// The thing that was missing between a client and an invoice. Without it the
/// only record that a client had agreed to anything was the invoice demanding
/// payment for it, which is the wrong way round: an invoice is evidence of a
/// bill, and a contract is the authority for one.
///
/// A draft is deliberately allowed to be incomplete. The figure and the dates
/// are the last things settled in a negotiation, and a record that cannot exist
/// until they are agreed is a record nobody opens until the week the work
/// starts — by which time the emails it was meant to replace are the only
/// history there is. So the terms are nullable, and
/// <see cref="Activate"/> is what refuses a contract that has not got any.
///
/// Once it is active the terms are frozen for the same reason a sent invoice is:
/// the client is holding a signed copy, and editing ours leaves the two
/// disagreeing with nothing to say which is right. What an active contract
/// accepts instead is <see cref="Extend"/>, which only ever moves the end date
/// later — which is what an extension letter does.
/// </remarks>
public sealed class Contract : Entity, IAuditable
{
    private Contract()
    {
        Reference = string.Empty;
        Title = string.Empty;
        Currency = string.Empty;
    }

    private Contract(Guid clientId, string reference, string title, string currency)
    {
        ClientId = clientId;
        Reference = Require(reference, nameof(reference));
        Title = Require(title, nameof(title));

        // Normalised through Money rather than stored as typed, so the column
        // cannot hold "kes " or "Kenyan shillings". The value goes in later and
        // has to be in this currency; a code Money would refuse would make that
        // impossible to enter, and the refusal would arrive weeks later.
        Currency = Common.Money.Zero(currency).Currency;

        State = ContractState.Draft;

        Raise(new ContractDrafted(Id, clientId, Reference, Title));
    }

    public static Contract Draft(Guid clientId, string reference, string title, string currency) =>
        new(clientId, reference, title, currency);

    public Guid ClientId { get; private init; }

    /// <summary>What both sides quote at each other. Unique, and never reused.</summary>
    public string Reference { get; private init; }

    /// <summary>What the agreement is for, in a line.</summary>
    public string Title { get; private set; }

    public string Currency { get; private init; }

    /// <summary>
    /// The agreed value, as a count of minor units. Null until it is agreed.
    /// </summary>
    /// <remarks>
    /// Two plain columns rather than a mapped value object, the same way a claim
    /// stores its amount: the type is what the code works with, the columns are
    /// what a report can sum and a person can read in a table viewer.
    /// </remarks>
    public long? MinorUnits { get; private set; }

    public DateOnly? StartsOn { get; private set; }

    public DateOnly? EndsOn { get; private set; }

    public ContractState State { get; private set; }

    public DateTimeOffset? ActivatedAt { get; private set; }

    /// <summary>When it was terminated, if it was.</summary>
    public DateTimeOffset? EndedAt { get; private set; }

    /// <summary>Why it was terminated.</summary>
    public string? Outcome { get; private set; }

    public Common.Money? Value =>
        MinorUnits is { } units ? Common.Money.Of(units, Currency) : null;

    /// <summary>Everything an active contract has to have.</summary>
    public bool HasTerms => MinorUnits is not null && StartsOn is not null && EndsOn is not null;

    /// <summary>
    /// Active, and past the date it was agreed to run to.
    /// </summary>
    /// <remarks>
    /// Asked of a day rather than stored, for the reason set out on
    /// <see cref="ContractState"/>: being expired is not something that happens
    /// to a contract, it is what is true of it at the moment somebody looks.
    /// </remarks>
    public bool HasExpiredOn(DateOnly today) =>
        State == ContractState.Active && EndsOn is { } ends && ends < today;

    /// <summary>
    /// Signed, started, and not yet over — so work on that day is covered.
    /// </summary>
    /// <remarks>
    /// The question anybody raising an invoice actually wants answered, and the
    /// reason the start date is checked as well as the end: a contract signed in
    /// November to begin in January authorises nothing in December.
    /// </remarks>
    public bool CoversOn(DateOnly day) =>
        State == ContractState.Active
        && StartsOn is { } starts
        && EndsOn is { } ends
        && starts <= day
        && day <= ends;

    /// <summary>
    /// What the firm may bill under it, all told.
    /// </summary>
    /// <remarks>
    /// A ceiling rather than a schedule. Splitting it into milestones is a real
    /// thing contracts do and is not modelled here, because the figure this
    /// system needs is the one somebody checks a run of invoices against, and a
    /// schedule that nobody keeps up to date answers that question wrongly
    /// rather than not at all.
    /// </remarks>
    public void WorthUpTo(Common.Money value)
    {
        Draftable();

        var agreed = InThisCurrency(value);

        if (agreed <= Common.Money.Zero(Currency))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), value, "A contract has to be worth something.");
        }

        MinorUnits = agreed.MinorUnits;
    }

    /// <summary>
    /// The span it covers.
    /// </summary>
    /// <remarks>
    /// Both dates together in one call, because the rule is about the pair. Set
    /// one at a time, a contract would pass through a moment of ending before it
    /// starts, and whichever of the two setters ran second would be the one
    /// blamed for a date the other one made wrong.
    /// </remarks>
    public void Runs(DateOnly startsOn, DateOnly endsOn)
    {
        Draftable();

        if (endsOn < startsOn)
        {
            throw new ArgumentException(
                $"It cannot end on {endsOn:d MMM yyyy}, before it starts on "
                + $"{startsOn:d MMM yyyy}.",
                nameof(endsOn));
        }

        StartsOn = startsOn;
        EndsOn = endsOn;
    }

    /// <summary>
    /// Signed. From here the firm may bill against it.
    /// </summary>
    /// <remarks>
    /// The value and the dates are required here rather than at construction
    /// because this is the moment they start to mean something. An active
    /// contract with no figure on it authorises any amount, and one with no
    /// dates authorises it for ever — both of which read on a screen as a
    /// contract in good order.
    /// </remarks>
    public void Activate(DateTimeOffset at)
    {
        if (State == ContractState.Terminated)
        {
            throw new InvalidOperationException(
                $"{Reference} was terminated and cannot be made active again. Raise a new "
                + "contract; the terminated one is what happened.");
        }

        if (State == ContractState.Active)
        {
            return;
        }

        if (MinorUnits is null)
        {
            throw new InvalidOperationException(
                $"{Reference} has no value on it. A contract nobody has priced cannot say what "
                + "this client may be billed.");
        }

        if (StartsOn is null || EndsOn is null)
        {
            throw new InvalidOperationException(
                $"{Reference} has no dates on it. A contract that never ends is one nobody can "
                + "be told has run out.");
        }

        State = ContractState.Active;
        ActivatedAt = at;

        Raise(new ContractActivated(
            Id, ClientId, Reference, MinorUnits.Value, Currency, StartsOn.Value, EndsOn.Value, at));
    }

    /// <summary>
    /// Move the end date later.
    /// </summary>
    /// <remarks>
    /// The one change an active contract accepts, and it only ever adds time. An
    /// extension is a letter both sides hold, so what it changes is exactly this
    /// one date; letting it move the date earlier would be a way to shorten an
    /// agreement unilaterally, and letting it change the value would be a way to
    /// rewrite what was signed while calling it an extension.
    ///
    /// Allowed after the end date has already passed, deliberately. Extensions
    /// are agreed late all the time, and refusing a backdated one leaves the
    /// only honest record of the arrangement outside this system.
    /// </remarks>
    public void Extend(DateOnly endsOn, DateTimeOffset at)
    {
        if (State != ContractState.Active)
        {
            throw new InvalidOperationException(
                $"{Reference} is {Word(State)} rather than active, so there is nothing to "
                + "extend.");
        }

        if (EndsOn is { } current && endsOn <= current)
        {
            throw new ArgumentException(
                $"{Reference} already runs to {current:d MMM yyyy}. An extension moves the end "
                + "later; terminate it if it is ending sooner than agreed.",
                nameof(endsOn));
        }

        EndsOn = endsOn;

        Raise(new ContractExtended(Id, ClientId, Reference, endsOn, at));
    }

    /// <summary>
    /// Ended early, by somebody, for a reason.
    /// </summary>
    /// <remarks>
    /// The reason is required and not merely encouraged. A terminated contract is
    /// the answer to "why did we stop billing them", and the person who asks that
    /// is never the person who ended it — so a blank reason means the answer is
    /// gone. The same rule, for the same reason, as a voided invoice and a
    /// project on hold.
    ///
    /// Allowed from draft as well as from active, which is the only way a
    /// negotiation that came to nothing can be closed. Without it abandoned
    /// drafts pile up indistinguishable from the ones still being argued over,
    /// and the list stops being worth opening.
    /// </remarks>
    public void Terminate(string reason, DateTimeOffset at)
    {
        if (State == ContractState.Terminated)
        {
            throw new InvalidOperationException(
                $"{Reference} is already terminated, and was terminated because: {Outcome}");
        }

        // The reason is checked before anything moves. Assigning it last would
        // leave a blank one having already terminated the contract in memory —
        // and the caller that caught the refusal and carried on saving would
        // write that state to the database with no reason on it, which is the
        // exact record this rule exists to prevent.
        var why = Require(reason, nameof(reason));
        var from = State;

        State = ContractState.Terminated;
        EndedAt = at;
        Outcome = why;

        Raise(new ContractTerminated(Id, ClientId, Reference, Outcome, from, at));
    }

    /// <summary>
    /// Fix the wording of what it is for.
    /// </summary>
    /// <remarks>
    /// Allowed in any state, because a title is how this firm finds the contract
    /// in a list and not a term either side agreed to. The reference, which is
    /// what both sides quote, is fixed.
    /// </remarks>
    public void Retitle(string title) => Title = Require(title, nameof(title));

    /// <summary>Nothing here is a secret from anybody who can see contracts.</summary>
    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    /// <summary>
    /// The same money, in this contract's currency.
    /// </summary>
    /// <remarks>
    /// Money refuses to compare two currencies, so a mismatch would throw
    /// anyway — with a message about arithmetic rather than about the contract.
    /// Caught here so the person typing it is told what is actually wrong.
    /// </remarks>
    private Common.Money InThisCurrency(Common.Money value) =>
        value.Currency == Currency
            ? value
            : throw new InvalidOperationException(
                $"{Reference} is in {Currency}, and that figure is in {value.Currency}. "
                + "One contract is in one currency.");

    private void Draftable()
    {
        if (State != ContractState.Draft)
        {
            throw new InvalidOperationException(
                $"{Reference} is {Word(State)} rather than a draft, so its terms cannot be "
                + "changed. Extend it if it is running on, or terminate it and agree another.");
        }
    }

    private static string Word(ContractState state) => state.ToString().ToLowerInvariant();

    private static string Require(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();
}

public sealed record ContractDrafted(
    Guid ContractId, Guid ClientId, string Reference, string Title) : DomainEvent;

public sealed record ContractActivated(
    Guid ContractId,
    Guid ClientId,
    string Reference,
    long MinorUnits,
    string Currency,
    DateOnly StartsOn,
    DateOnly EndsOn,
    DateTimeOffset At) : DomainEvent;

public sealed record ContractExtended(
    Guid ContractId,
    Guid ClientId,
    string Reference,
    DateOnly EndsOn,
    DateTimeOffset At) : DomainEvent;

public sealed record ContractTerminated(
    Guid ContractId,
    Guid ClientId,
    string Reference,
    string Reason,
    ContractState From,
    DateTimeOffset At) : DomainEvent;
