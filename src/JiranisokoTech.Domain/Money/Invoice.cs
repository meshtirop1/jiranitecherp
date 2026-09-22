using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Money;

public enum InvoiceStatus
{
    /// <summary>Being put together. Nobody outside the firm has seen it.</summary>
    Draft = 1,

    Sent = 2,
    PartlyPaid = 3,
    Paid = 4,

    /// <summary>Cancelled after being sent, with a reason.</summary>
    Void = 5,
}

/// <summary>
/// A bill, and what has been paid against it.
/// </summary>
/// <remarks>
/// The rule that shapes everything here: once an invoice has been sent, it does
/// not change. Somebody outside this firm is holding a document with these
/// figures on it, and editing our copy leaves the two disagreeing with nothing
/// to say which is right. A wrong invoice is voided and replaced, which is also
/// what an accountant expects to find.
/// </remarks>
public sealed class Invoice : Entity, IAuditable
{
    private readonly List<InvoiceLine> _lines = [];
    private readonly List<Payment> _payments = [];

    private Invoice()
    {
        Number = string.Empty;
        Currency = string.Empty;
    }

    private Invoice(Guid clientId, string number, string currency, DateOnly issuedOn, DateOnly dueOn)
    {
        if (dueOn < issuedOn)
        {
            throw new ArgumentException("It cannot fall due before it is issued.", nameof(dueOn));
        }

        ClientId = clientId;
        Number = Require(number, nameof(number));
        Currency = currency;
        IssuedOn = issuedOn;
        DueOn = dueOn;
        Status = InvoiceStatus.Draft;
    }

    public static Invoice Draft(
        Guid clientId, string number, string currency, DateOnly issuedOn, int paymentTermDays) =>
        new(clientId, number, currency, issuedOn, issuedOn.AddDays(paymentTermDays));

    public Guid ClientId { get; private init; }

    /// <summary>What the client quotes back at us. Unique, and never reused.</summary>
    public string Number { get; private init; }

    public string Currency { get; private init; }

    public DateOnly IssuedOn { get; private init; }

    public DateOnly DueOn { get; private set; }

    public InvoiceStatus Status { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    public string? Outcome { get; private set; }

    /// <remarks>
    /// A copy, so that nothing outside this invoice can add a line to it. Every
    /// rule about a line — the currency, the quantity, whether the invoice is
    /// still a draft — lives in <see cref="AddLine"/>, and a caller holding the
    /// real list could walk past all of them.
    /// </remarks>
    public IReadOnlyList<InvoiceLine> Lines => _lines.ToList();

    /// <inheritdoc cref="Lines"/>
    public IReadOnlyList<Payment> Payments => _payments.ToList();

    public Common.Money Total => _lines.Count == 0
        ? Common.Money.Zero(Currency)
        : _lines.Aggregate(
            Common.Money.Zero(Currency), (running, line) => running + line.Amount);

    public Common.Money Paid => _payments.Count == 0
        ? Common.Money.Zero(Currency)
        : _payments.Aggregate(
            Common.Money.Zero(Currency),
            (running, payment) => running + Common.Money.Of(payment.MinorUnits, Currency));

    public Common.Money Outstanding => Total - Paid;

    public bool IsSettled => Status is InvoiceStatus.Paid or InvoiceStatus.Void;

    /// <summary>Sent, due, and not yet paid.</summary>
    public bool IsOverdue(DateOnly today) =>
        Status is InvoiceStatus.Sent or InvoiceStatus.PartlyPaid && DueOn < today;

    public void AddLine(string description, int quantity, Common.Money unitPrice)
    {
        Draftable();

        if (quantity < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(quantity), quantity, "A line is for at least one of something.");
        }

        _lines.Add(InvoiceLine.Of(description, quantity, InThisCurrency(unitPrice)));
    }

    public void RemoveLine(Guid lineId)
    {
        Draftable();

        _lines.RemoveAll(line => line.Id == lineId);
    }

    /// <summary>
    /// Send it, and freeze it.
    /// </summary>
    /// <remarks>
    /// An empty invoice is refused. Sending a client a bill for nothing is a
    /// conversation nobody wants to have, and it is always a mistake rather
    /// than an intention.
    /// </remarks>
    public void Send(DateTimeOffset at)
    {
        Draftable();

        if (_lines.Count == 0)
        {
            throw new InvalidOperationException(
                "There is nothing on this invoice. Add a line before sending it.");
        }

        Status = InvoiceStatus.Sent;
        SentAt = at;

        Raise(new InvoiceSent(Id, ClientId, Number, Total.MinorUnits, Currency, DueOn, at));
    }

    /// <summary>
    /// Record money received.
    /// </summary>
    /// <remarks>
    /// Overpayment is refused. A client paying more than the bill is either a
    /// mistake on their side or ours, and quietly absorbing it into a paid
    /// invoice loses the difference — which somebody then finds months later in
    /// a reconciliation with nothing to explain it.
    /// </remarks>
    public void RecordPayment(Common.Money amount, DateOnly on, string? reference, DateTimeOffset at)
    {
        if (Status is InvoiceStatus.Draft)
        {
            throw new InvalidOperationException(
                "This invoice has not been sent, so nobody can have paid it.");
        }

        if (Status is InvoiceStatus.Void)
        {
            throw new InvalidOperationException("This invoice was voided.");
        }

        var paid = InThisCurrency(amount);

        if (paid <= Common.Money.Zero(Currency))
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount), amount, "A payment has to be for something.");
        }

        if (paid > Outstanding)
        {
            throw new InvalidOperationException(
                $"That is more than is outstanding ({Outstanding}). Record what was received, "
                + "and raise a credit for the difference.");
        }

        _payments.Add(Payment.Of(paid.MinorUnits, on, reference));

        Status = Outstanding <= Common.Money.Zero(Currency)
            ? InvoiceStatus.Paid
            : InvoiceStatus.PartlyPaid;

        Raise(new PaymentRecorded(
            Id, ClientId, Number, paid.MinorUnits, Currency, Outstanding.MinorUnits, at));

        if (Status == InvoiceStatus.Paid)
        {
            Raise(new InvoiceSettled(Id, ClientId, Number, Total.MinorUnits, Currency, at));
        }
    }

    /// <summary>
    /// Cancel a sent invoice.
    /// </summary>
    /// <remarks>
    /// Refused once anything has been paid against it. At that point there is
    /// money in the bank with this number on it, and voiding the invoice leaves
    /// a payment that belongs to nothing.
    /// </remarks>
    public void Void(string reason, DateTimeOffset at)
    {
        if (Status == InvoiceStatus.Void)
        {
            return;
        }

        if (_payments.Count > 0)
        {
            throw new InvalidOperationException(
                "Money has been received against this invoice, so it cannot be voided. Raise a "
                + "credit note instead.");
        }

        Status = InvoiceStatus.Void;
        Outcome = Require(reason, nameof(reason));

        Raise(new InvoiceVoided(Id, ClientId, Number, Outcome, at));
    }

    public void DueBy(DateOnly on)
    {
        Draftable();

        if (on < IssuedOn)
        {
            throw new ArgumentException("It cannot fall due before it is issued.", nameof(on));
        }

        DueOn = on;
    }

    /// <summary>Nothing here is a secret from anybody who can see invoices.</summary>
    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    /// <summary>
    /// The same money, in this invoice currency.
    /// </summary>
    /// <remarks>
    /// Money refuses to add two currencies, so a mismatch would throw anyway —
    /// with a message about arithmetic rather than about the invoice. Caught
    /// here so the person typing it is told what is actually wrong.
    /// </remarks>
    private Common.Money InThisCurrency(Common.Money amount) =>
        amount.Currency == Currency
            ? amount
            : throw new InvalidOperationException(
                $"This invoice is in {Currency}, and that figure is in {amount.Currency}. "
                + "One invoice is in one currency.");

    private void Draftable()
    {
        if (Status != InvoiceStatus.Draft)
        {
            throw new InvalidOperationException(
                "This invoice has been sent and cannot be changed. Void it and raise another if "
                + "the figures were wrong.");
        }
    }

    private static string Require(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();
}

/// <summary>One thing being charged for.</summary>
public sealed class InvoiceLine
{
    private InvoiceLine()
    {
        Description = string.Empty;
        Currency = string.Empty;
    }

    internal static InvoiceLine Of(string description, int quantity, Common.Money unitPrice) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Description = string.IsNullOrWhiteSpace(description)
                ? throw new ArgumentException("Say what it is for.", nameof(description))
                : description.Trim(),
            Quantity = quantity,
            UnitMinorUnits = unitPrice.MinorUnits,
            Currency = unitPrice.Currency,
        };

    public Guid Id { get; private init; }

    public string Description { get; private init; }

    public int Quantity { get; private init; }

    public long UnitMinorUnits { get; private init; }

    public string Currency { get; private init; }

    public Common.Money UnitPrice => Common.Money.Of(UnitMinorUnits, Currency);

    /// <summary>
    /// Multiplied in integers, so nothing rounds.
    /// </summary>
    /// <remarks>
    /// Money.Times takes an int for exactly this: a quantity is a count, and
    /// counting in decimals is how a line comes out a cent short.
    /// </remarks>
    public Common.Money Amount => UnitPrice.Times(Quantity);
}

/// <summary>Money received against an invoice.</summary>
public sealed class Payment
{
    private Payment()
    {
    }

    internal static Payment Of(long minorUnits, DateOnly on, string? reference) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            MinorUnits = minorUnits,
            On = on,
            Reference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(),
        };

    public Guid Id { get; private init; }

    public long MinorUnits { get; private init; }

    public DateOnly On { get; private init; }

    /// <summary>The bank reference, so a payment can be found again.</summary>
    public string? Reference { get; private init; }
}

public sealed record InvoiceSent(
    Guid InvoiceId,
    Guid ClientId,
    string Number,
    long MinorUnits,
    string Currency,
    DateOnly DueOn,
    DateTimeOffset At) : DomainEvent;

public sealed record PaymentRecorded(
    Guid InvoiceId,
    Guid ClientId,
    string Number,
    long MinorUnits,
    string Currency,
    long OutstandingMinorUnits,
    DateTimeOffset At) : DomainEvent;

public sealed record InvoiceSettled(
    Guid InvoiceId,
    Guid ClientId,
    string Number,
    long MinorUnits,
    string Currency,
    DateTimeOffset At) : DomainEvent;

public sealed record InvoiceVoided(
    Guid InvoiceId, Guid ClientId, string Number, string Reason, DateTimeOffset At) : DomainEvent;
