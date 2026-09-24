using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Procurement;

/// <summary>Where an order has got to.</summary>
public enum PurchaseOrderState
{
    /// <summary>Being put together. The only state in which the lines can change.</summary>
    Draft = 1,

    /// <summary>Sent to the supplier. The figures are now theirs as well as ours.</summary>
    Placed = 2,

    /// <summary>Everything ordered has arrived or been written off.</summary>
    Complete = 3,

    Cancelled = 4,
}

/// <summary>What condition something arrived in.</summary>
public enum ReceivedCondition
{
    Good = 1,

    /// <summary>Arrived, and not as it should have.</summary>
    Damaged = 2,

    /// <summary>Arrived and is not what was ordered.</summary>
    Wrong = 3,
}

/// <summary>One thing being bought.</summary>
public sealed class PurchaseOrderLine : Entity
{
    private PurchaseOrderLine() => Description = string.Empty;

    internal PurchaseOrderLine(
        string description, int quantity, Common.Money unit, Guid? requestLineId)
    {
        if (quantity < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(quantity), quantity, "A line is for at least one of something.");
        }

        Description = string.IsNullOrWhiteSpace(description)
            ? throw new ArgumentException("Say what is being bought.", nameof(description))
            : description.Trim();

        Quantity = quantity;
        UnitMinorUnits = unit.MinorUnits;
        Currency = unit.Currency;
        RequestLineId = requestLineId;
    }

    public string Description { get; private init; }

    public int Quantity { get; private init; }

    public long UnitMinorUnits { get; private init; }

    public string Currency { get; private init; } = string.Empty;

    /// <summary>
    /// The request line this came from, when it came from one.
    /// </summary>
    /// <remarks>
    /// On the line rather than on the order, because one order routinely covers lines from two
    /// requests — combined because the price was better together — and a single request id on
    /// the header could not express that. It is what lets the request keep its own shape while
    /// still being answerable about what was actually bought.
    /// </remarks>
    public Guid? RequestLineId { get; private init; }

    /// <summary>How many have been written off as never going to arrive.</summary>
    public int Short { get; private set; }

    public Common.Money Unit => Common.Money.Of(UnitMinorUnits, Currency);

    public Common.Money Amount => Unit.Times(Quantity);

    internal void WriteOff(int quantity)
    {
        Short += quantity;
    }
}

/// <summary>
/// What actually arrived, on a day, in a condition.
/// </summary>
/// <remarks>
/// Appended and never edited. The condition is a fact about a moment rather than about the order,
/// and the argument it settles six months later — was the screen already cracked when it came —
/// is one only a dated record can answer.
/// </remarks>
public sealed class GoodsReceipt : Entity
{
    private GoodsReceipt()
    {
    }

    internal GoodsReceipt(
        Guid orderLineId,
        int quantity,
        DateOnly receivedOn,
        ReceivedCondition condition,
        Guid receivedById,
        string? note)
    {
        if (quantity < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(quantity), quantity, "Receiving nothing is not an event.");
        }

        OrderLineId = orderLineId;
        Quantity = quantity;
        ReceivedOn = receivedOn;
        Condition = condition;
        ReceivedById = receivedById;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }

    public Guid OrderLineId { get; private init; }

    public int Quantity { get; private init; }

    /// <summary>
    /// The day it arrived.
    /// </summary>
    /// <remarks>
    /// A date rather than a timestamp, because it is often typed in on the Monday for something
    /// that came on the Friday, and a timestamp would be a precise record of when somebody got
    /// round to the paperwork.
    /// </remarks>
    public DateOnly ReceivedOn { get; private init; }

    public ReceivedCondition Condition { get; private init; }

    public Guid ReceivedById { get; private init; }

    public string? Note { get; private init; }
}

/// <summary>Common.Money that went out to the supplier.</summary>
/// <remarks>
/// Records that a payment happened; pays nothing. Nothing in this system touches a bank, and a
/// control that looked like it moved money and did not would be the most dangerous one here.
/// </remarks>
public sealed class VendorPayment : Entity
{
    private VendorPayment()
    {
    }

    internal VendorPayment(Common.Money amount, DateOnly paidOn, string? reference)
    {
        MinorUnits = amount.MinorUnits;
        Currency = amount.Currency;
        PaidOn = paidOn;
        Reference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();
    }

    public long MinorUnits { get; private init; }

    public string Currency { get; private init; } = string.Empty;

    public DateOnly PaidOn { get; private init; }

    /// <summary>The bank reference, so a payment can be found again from a statement.</summary>
    public string? Reference { get; private init; }

    public Common.Money Amount => Common.Money.Of(MinorUnits, Currency);
}

/// <summary>
/// The firm committing to buy something from a supplier.
/// </summary>
/// <remarks>
/// Section 61, second of two aggregates, and the one that carries what actually happened.
///
/// <b>Receiving is not a third aggregate.</b> It is an append-only collection here, because a
/// receipt has no life of its own: it is never edited, never has a state, and means nothing away
/// from the line it is against. Making it an aggregate would buy a table and cost the invariant
/// that matters — that what was received can never exceed what was ordered.
///
/// <b>Ordered = received + short + outstanding, per line, always.</b> Received is summed from the
/// receipts and short is what somebody wrote off; nothing stores "outstanding", because a stored
/// remainder is a figure that can disagree with the receipts it was derived from. Ordering ten
/// laptops and getting seven leaves three outstanding, and the order says so until somebody
/// either receives them or writes them off with a reason — it never quietly decides the order is
/// finished.
///
/// <b>Frozen on placement.</b> Once it is sent the supplier is holding a document with these
/// figures on it, and editing our copy leaves the two disagreeing with nothing to say which is
/// right.
/// </remarks>
public sealed class PurchaseOrder : Entity, IAuditable
{
    private readonly List<PurchaseOrderLine> _lines = [];
    private readonly List<GoodsReceipt> _receipts = [];
    private readonly List<VendorPayment> _payments = [];

    private PurchaseOrder()
    {
        Number = string.Empty;
    }

    private PurchaseOrder(
        string number, Guid vendorId, Guid accountId, Guid raisedById, DateTimeOffset at)
    {
        Number = string.IsNullOrWhiteSpace(number)
            ? throw new ArgumentException("An order needs a number.", nameof(number))
            : number.Trim();

        VendorId = vendorId;
        AccountId = accountId;
        RaisedById = raisedById;
        RaisedAt = at;
        State = PurchaseOrderState.Draft;
    }

    public static PurchaseOrder Raise(
        string number, Guid vendorId, Guid accountId, Guid raisedById, DateTimeOffset at) =>
        new(number, vendorId, accountId, raisedById, at);

    public string Number { get; private init; }

    public Guid VendorId { get; private set; }

    /// <summary>
    /// The expense account it goes against.
    /// </summary>
    /// <remarks>
    /// Required, not nullable, and the reason is the one a standing cost already gives: without
    /// it the spending cannot appear on the report it exists to appear on, and an order with no
    /// account is money the firm spent and cannot categorise.
    /// </remarks>
    public Guid AccountId { get; private set; }

    public Guid RaisedById { get; private init; }

    public PurchaseOrderState State { get; private set; }

    public DateTimeOffset RaisedAt { get; private init; }

    public DateTimeOffset? PlacedAt { get; private set; }

    public DateOnly? ExpectedOn { get; private set; }

    public string? Notes { get; private set; }

    public string? Outcome { get; private set; }

    public IReadOnlyList<PurchaseOrderLine> Lines => [.. _lines.OrderBy(one => one.Id)];

    public IReadOnlyList<GoodsReceipt> Receipts =>
        [.. _receipts.OrderBy(one => one.ReceivedOn).ThenBy(one => one.Id)];

    public IReadOnlyList<VendorPayment> Payments =>
        [.. _payments.OrderBy(one => one.PaidOn).ThenBy(one => one.Id)];

    public bool IsDraft => State == PurchaseOrderState.Draft;

    public bool IsPlaced => State == PurchaseOrderState.Placed;

    public string? Currency => _lines.Count == 0 ? null : _lines[0].Currency;

    public Common.Money? Total =>
        _lines.Count == 0
            ? null
            : _lines.Aggregate(
                Common.Money.Zero(_lines[0].Currency), (running, line) => running + line.Amount);

    public Common.Money? Paid =>
        _payments.Count == 0
            ? null
            : _payments.Aggregate(
                Common.Money.Zero(_payments[0].Currency), (running, one) => running + one.Amount);

    /// <summary>How many of one line have arrived.</summary>
    public int ReceivedOnLine(Guid lineId) =>
        _receipts.Where(one => one.OrderLineId == lineId).Sum(one => one.Quantity);

    /// <summary>
    /// How many of one line are still to come.
    /// </summary>
    /// <remarks>
    /// Derived, never stored. A remainder kept as a column is a figure that can disagree with the
    /// receipts it came from, and the day it does nobody can tell which is wrong — the accounting
    /// decision, applied to quantities.
    /// </remarks>
    public int OutstandingOn(Guid lineId)
    {
        var line = _lines.FirstOrDefault(one => one.Id == lineId)
            ?? throw new InvalidOperationException("That line is not on this order.");

        return line.Quantity - ReceivedOnLine(lineId) - line.Short;
    }

    /// <summary>Nothing ordered is still to come.</summary>
    public bool EverythingSettled => _lines.All(one => OutstandingOn(one.Id) == 0);

    public PurchaseOrderLine Add(
        string description, int quantity, Common.Money unit, Guid? requestLineId = null)
    {
        Editable();

        if (_lines.Count > 0 && _lines[0].Currency != unit.Currency)
        {
            throw new InvalidOperationException(
                $"This order is in {_lines[0].Currency} and that line is in {unit.Currency}. An "
                + "order is in one currency — the supplier's — so raise a second order for "
                + "anything priced in another.");
        }

        var line = new PurchaseOrderLine(description, quantity, unit, requestLineId);

        _lines.Add(line);

        return line;
    }

    public void Remove(Guid lineId)
    {
        Editable();

        _lines.RemoveAll(one => one.Id == lineId);
    }

    public void Describe(Guid accountId, DateOnly? expectedOn, string? notes)
    {
        Editable();

        AccountId = accountId;
        ExpectedOn = expectedOn;
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
    }

    /// <summary>Send it to the supplier.</summary>
    public void Place(DateTimeOffset at)
    {
        Editable();

        if (_lines.Count == 0)
        {
            throw new InvalidOperationException(
                "There is nothing on this order. Add a line before placing it.");
        }

        State = PurchaseOrderState.Placed;
        PlacedAt = at;

        Raise(new PurchaseOrderPlaced(
            Id, Number, VendorId, Total!.Value.MinorUnits, Total!.Value.Currency, at));
    }

    /// <summary>
    /// Record what arrived.
    /// </summary>
    /// <remarks>
    /// Refused beyond what is outstanding, and the message says both numbers. Receiving twelve
    /// against an order for ten is either a miscount or a supplier sending more than was asked
    /// for, and both are conversations rather than something to record silently.
    /// </remarks>
    public GoodsReceipt Receive(
        Guid lineId,
        int quantity,
        DateOnly receivedOn,
        ReceivedCondition condition,
        Guid receivedById,
        string? note = null)
    {
        if (!IsPlaced)
        {
            throw new InvalidOperationException(
                State == PurchaseOrderState.Draft
                    ? "This order has not been placed, so nothing can have arrived against it."
                    : $"{Number} is {State.ToString().ToLowerInvariant()}.");
        }

        var outstanding = OutstandingOn(lineId);

        if (quantity > outstanding)
        {
            throw new InvalidOperationException(
                $"Only {outstanding} of that line {(outstanding == 1 ? "is" : "are")} still to "
                + $"come and {quantity} was entered. Either the count is wrong or the supplier "
                + "has sent more than was ordered, and both are worth asking about.");
        }

        var receipt = new GoodsReceipt(
            lineId, quantity, receivedOn, condition, receivedById, note);

        _receipts.Add(receipt);

        if (EverythingSettled)
        {
            State = PurchaseOrderState.Complete;
            Outcome = "Everything ordered has arrived or been written off.";
        }

        return receipt;
    }

    /// <summary>
    /// Write off what is never going to arrive, saying why.
    /// </summary>
    /// <remarks>
    /// The honest end for an order where three of ten never came. Without it the only ways to
    /// close are to pretend they arrived — which puts three things on the register that do not
    /// exist — or to leave the order open for ever, which is how a purchasing screen fills with
    /// rows nobody can act on.
    /// </remarks>
    public void CloseShort(Guid lineId, string why, DateTimeOffset at)
    {
        if (!IsPlaced)
        {
            throw new InvalidOperationException("Only a placed order can be closed short.");
        }

        var line = _lines.FirstOrDefault(one => one.Id == lineId)
            ?? throw new InvalidOperationException("That line is not on this order.");

        var outstanding = OutstandingOn(lineId);

        if (outstanding == 0)
        {
            throw new InvalidOperationException(
                "Nothing is outstanding on that line, so there is nothing to write off.");
        }

        if (string.IsNullOrWhiteSpace(why))
        {
            throw new ArgumentException(
                "Say why it is not coming. A quantity written off with no reason is one nobody "
                + "can tell from a counting mistake.",
                nameof(why));
        }

        line.WriteOff(outstanding);

        Outcome = why.Trim();

        if (EverythingSettled)
        {
            State = PurchaseOrderState.Complete;
        }
    }

    /// <summary>
    /// Record that the supplier was paid.
    /// </summary>
    /// <remarks>
    /// Records; does not pay. Overpayment is refused the way an invoice refuses it — paying more
    /// than was ordered is either a duplicate entry or a mistake at the bank, and neither should
    /// be absorbed silently.
    /// </remarks>
    public VendorPayment Pay(Common.Money amount, DateOnly paidOn, string? reference = null)
    {
        if (State is PurchaseOrderState.Draft or PurchaseOrderState.Cancelled)
        {
            throw new InvalidOperationException(
                "Only an order that has been placed can have been paid.");
        }

        if (Total is not { } total)
        {
            throw new InvalidOperationException("There is nothing on this order to pay for.");
        }

        if (amount.Currency != total.Currency)
        {
            throw new InvalidOperationException(
                $"This order is in {total.Currency} and the payment is in {amount.Currency}. "
                + "Nothing here converts between currencies.");
        }

        var already = Paid ?? Common.Money.Zero(total.Currency);

        if (already.MinorUnits + amount.MinorUnits > total.MinorUnits)
        {
            throw new InvalidOperationException(
                "That would pay more than the order is for. Check the figure — an overpayment "
                + "is nearly always the same payment entered twice.");
        }

        var payment = new VendorPayment(amount, paidOn, reference);

        _payments.Add(payment);

        return payment;
    }

    public void Cancel(string why, DateTimeOffset at)
    {
        if (_receipts.Count > 0)
        {
            throw new InvalidOperationException(
                "Something has already arrived against this order, so it cannot be cancelled. "
                + "Close the outstanding lines short instead, which keeps what did arrive.");
        }

        State = PurchaseOrderState.Cancelled;
        Outcome = string.IsNullOrWhiteSpace(why)
            ? throw new ArgumentException("Say why it was cancelled.", nameof(why))
            : why.Trim();
    }

    /// <summary>
    /// What the firm bought is not a secret from anybody who can see the purchasing.
    /// </summary>
    /// <remarks>
    /// Unlike a payslip, an order is the firm's own spending rather than a person's pay, and the
    /// whole value of the trail here is being able to say who changed a figure before it was
    /// sent.
    /// </remarks>
    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private void Editable()
    {
        if (!IsDraft)
        {
            throw new InvalidOperationException(
                $"{Number} has been placed. The supplier is holding a document with these "
                + "figures on it, so changing our copy would leave the two disagreeing.");
        }
    }
}

public sealed record PurchaseOrderPlaced(
    Guid OrderId,
    string Number,
    Guid VendorId,
    long MinorUnits,
    string Currency,
    DateTimeOffset At) : DomainEvent;
