using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Procurement;

/// <summary>Where a request to buy something has got to.</summary>
public enum PurchaseRequestState
{
    /// <summary>Being written. The only state in which the lines can change.</summary>
    Draft = 1,

    /// <summary>Sent for approval and waiting on somebody.</summary>
    Submitted = 2,

    Approved = 3,

    Refused = 4,

    /// <summary>Taken back by whoever asked, before it was decided.</summary>
    Withdrawn = 5,
}

/// <summary>
/// One thing somebody is asking the firm to buy.
/// </summary>
/// <remarks>
/// Part of section 61. A line, not a whole request, because one request routinely splits across
/// two suppliers and one order routinely combines lines from two requests.
/// </remarks>
public sealed class PurchaseRequestLine : Entity
{
    private PurchaseRequestLine() => Description = string.Empty;

    internal PurchaseRequestLine(
        string description, int quantity, Common.Money estimate)
    {
        if (quantity < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(quantity), quantity, "A line is for at least one of something.");
        }

        Description = string.IsNullOrWhiteSpace(description)
            ? throw new ArgumentException("Say what is being asked for.", nameof(description))
            : description.Trim();

        Quantity = quantity;
        UnitMinorUnits = estimate.MinorUnits;
        Currency = estimate.Currency;
    }

    public string Description { get; private init; }

    public int Quantity { get; private init; }

    public long UnitMinorUnits { get; private init; }

    public string Currency { get; private init; } = string.Empty;

    public Common.Money Unit => Common.Money.Of(UnitMinorUnits, Currency);

    /// <summary>
    /// What the line is expected to cost.
    /// </summary>
    /// <remarks>
    /// Multiplied in integers rather than through a decimal, because rounding a unit price and
    /// then multiplying is how a total ends up a shilling out — the reason Money.Times takes an
    /// int at all.
    /// </remarks>
    public Common.Money Estimate => Unit.Times(Quantity);
}

/// <summary>
/// Somebody asking the firm to buy something.
/// </summary>
/// <remarks>
/// Section 61, first of two aggregates. A request and an order are separate records rather than
/// one record with more states, and that is the decision the section turns on.
///
/// <b>The pipeline precedent does not apply here, and it is worth saying why.</b> Section 16
/// keeps an enquiry and an opportunity as one row precisely so nothing is lost to a conversion
/// step — but that works because they are one-to-one: one enquiry becomes one opportunity. These
/// are not. One request splits across two suppliers; one order combines lines from two requests
/// because the price was better together; one order is filled by three deliveries. A single
/// record with states cannot express any of those without lying about one of them.
///
/// <b>What does transfer from that precedent is that nothing is consumed.</b> Raising an order
/// does not close, hollow out or rewrite the request — the order's line points back at the
/// request's line, and the request stays exactly as its approver read it. Section 16's remark
/// that "every conversion step in every system ever built loses the history" is the thing being
/// avoided, by a different means.
///
/// <b>There is no column saying whether it has been ordered.</b> "Approved and nobody ordered it"
/// is a query over the orders, not a flag — the accounting rule restated: a stored figure is one
/// that can disagree with the documents it was added up from, and the day it does nobody can tell
/// which is wrong.
/// </remarks>
public sealed class PurchaseRequest : Entity, IAuditable
{
    private readonly List<PurchaseRequestLine> _lines = [];

    private PurchaseRequest()
    {
        Reference = string.Empty;
        Justification = string.Empty;
    }

    private PurchaseRequest(
        string reference,
        Guid raisedById,
        string justification,
        DateOnly? neededBy,
        DateTimeOffset at)
    {
        Reference = string.IsNullOrWhiteSpace(reference)
            ? throw new ArgumentException("A request needs a reference.", nameof(reference))
            : reference.Trim();

        RaisedById = raisedById;
        Justification = Require(justification, nameof(justification), 4_000);
        NeededBy = neededBy;
        RaisedAt = at;
        State = PurchaseRequestState.Draft;
    }

    public static PurchaseRequest Raise(
        string reference,
        Guid raisedById,
        string justification,
        DateTimeOffset at,
        DateOnly? neededBy = null) =>
        new(reference, raisedById, justification, neededBy, at);

    public string Reference { get; private init; }

    public Guid RaisedById { get; private init; }

    /// <summary>
    /// Why the firm should spend this money.
    /// </summary>
    /// <remarks>
    /// Required, and it is the field the whole request exists to carry. An approver deciding on a
    /// list of items and a total is deciding on arithmetic; the argument is what they are
    /// actually being asked about, and a request that does not make one gets approved out of
    /// politeness.
    /// </remarks>
    public string Justification { get; private set; }

    public DateOnly? NeededBy { get; private set; }

    public PurchaseRequestState State { get; private set; }

    public DateTimeOffset RaisedAt { get; private init; }

    public DateTimeOffset? SubmittedAt { get; private set; }

    public DateTimeOffset? SettledAt { get; private set; }

    public string? Outcome { get; private set; }

    public IReadOnlyList<PurchaseRequestLine> Lines => [.. _lines.OrderBy(one => one.Id)];

    public bool IsDraft => State == PurchaseRequestState.Draft;

    public bool IsApproved => State == PurchaseRequestState.Approved;

    /// <summary>What it is expected to cost, summed from the lines.</summary>
    /// <remarks>
    /// Null when there are no lines, because a total of zero and a request with nothing on it
    /// read the same on a screen and mean different things.
    /// </remarks>
    public Common.Money? Estimate =>
        _lines.Count == 0
            ? null
            : _lines.Aggregate(
                Common.Money.Zero(_lines[0].Currency), (running, line) => running + line.Estimate);

    /// <summary>
    /// Add something to the list.
    /// </summary>
    /// <remarks>
    /// One currency per request, caught here with a sentence naming both codes. Money would throw
    /// anyway on the sum, but with a message about arithmetic rather than about the request —
    /// which is the same reason an invoice checks it at the line rather than at the total.
    /// </remarks>
    public PurchaseRequestLine Add(string description, int quantity, Common.Money estimate)
    {
        Editable();

        if (_lines.Count > 0 && _lines[0].Currency != estimate.Currency)
        {
            throw new InvalidOperationException(
                $"This request is in {_lines[0].Currency} and that line is in "
                + $"{estimate.Currency}. A request is in one currency; raise a second one for "
                + "anything bought in another.");
        }

        var line = new PurchaseRequestLine(description, quantity, estimate);

        _lines.Add(line);

        return line;
    }

    public void Remove(Guid lineId)
    {
        Editable();

        _lines.RemoveAll(one => one.Id == lineId);
    }

    public void Describe(string justification, DateOnly? neededBy)
    {
        Editable();

        Justification = Require(justification, nameof(justification), 4_000);
        NeededBy = neededBy;
    }

    /// <summary>
    /// Send it for approval.
    /// </summary>
    /// <remarks>
    /// A request with nothing on it cannot be sent — the invoice's rule, for the invoice's
    /// reason: somebody is being asked to approve spending and there is nothing to approve.
    /// </remarks>
    public void Submit(DateTimeOffset at)
    {
        Editable();

        if (_lines.Count == 0)
        {
            throw new InvalidOperationException(
                "There is nothing on this request. Add a line before sending it for approval.");
        }

        State = PurchaseRequestState.Submitted;
        SubmittedAt = at;

        var estimate = Estimate!.Value;

        Raise(new PurchaseRequested(
            Id, Reference, RaisedById, estimate.MinorUnits, estimate.Currency, at));
    }

    public void Approved(DateTimeOffset at, string? note = null)
    {
        Settling();

        State = PurchaseRequestState.Approved;
        SettledAt = at;
        Outcome = Trimmed(note);
    }

    public void Refused(string why, DateTimeOffset at)
    {
        Settling();

        State = PurchaseRequestState.Refused;
        SettledAt = at;
        Outcome = Require(why, nameof(why), 1_000);
    }

    /// <summary>Taken back by whoever asked for it.</summary>
    public void Withdraw(string why, DateTimeOffset at)
    {
        if (State is PurchaseRequestState.Approved or PurchaseRequestState.Refused)
        {
            throw new InvalidOperationException(
                "This has already been decided. Withdrawing it now would hide a decision "
                + "somebody took.");
        }

        State = PurchaseRequestState.Withdrawn;
        SettledAt = at;
        Outcome = Require(why, nameof(why), 1_000);
    }

    /// <summary>Nothing here is a secret from anybody who can see the purchasing.</summary>
    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    /// <summary>
    /// The lines can only change while it is a draft.
    /// </summary>
    /// <remarks>
    /// Once it is submitted the list is what the approver read, and changing it afterwards is the
    /// fault an invoice's draft rule exists to prevent — somebody approves one thing and the
    /// record says another, with nothing to show the two were ever different.
    /// </remarks>
    private void Editable()
    {
        if (!IsDraft)
        {
            throw new InvalidOperationException(
                $"{Reference} has been sent for approval. What is on it is what somebody is "
                + "deciding about, so it cannot change now — withdraw it and raise another.");
        }
    }

    private void Settling()
    {
        if (State != PurchaseRequestState.Submitted)
        {
            throw new InvalidOperationException(
                $"{Reference} is not waiting on a decision.");
        }
    }

    private static string Require(string value, string parameter, int longest)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("This cannot be blank.", parameter);
        }

        var trimmed = value.Trim();

        return trimmed.Length > longest
            ? throw new ArgumentException(
                $"This cannot be longer than {longest} characters.", parameter)
            : trimmed;
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Somebody has asked the firm to buy something.
/// </summary>
/// <remarks>
/// Carries the total so a handler deciding how long the approval chain should be does not have to
/// go and sum the lines again — and so the figure the chain was opened against is the figure that
/// was submitted, rather than whatever the lines say by the time the handler runs.
/// </remarks>
public sealed record PurchaseRequested(
    Guid RequestId,
    string Reference,
    Guid RaisedById,
    long MinorUnits,
    string Currency,
    DateTimeOffset At) : DomainEvent;
