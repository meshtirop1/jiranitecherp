using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Assets;

using Money = JiranisokoTech.Domain.Common.Money;

/// <summary>
/// What sort of thing it is.
/// </summary>
/// <remarks>
/// Moved here from <c>Domain.People</c>, where it lived because the only things this system knew
/// about equipment were what a leaver had to give back. That is the wrong way round — a laptop
/// is a thing the firm owns, and who is holding it today is one fact about it — and section 15
/// is the correction.
///
/// Six, and <see cref="Other"/> is load-bearing. A list that tried to name every category would
/// be a list somebody maintains instead of recording what they bought.
/// </remarks>
public enum AssetKind
{
    Laptop = 1,
    Phone = 2,
    Monitor = 3,

    /// <summary>A door key, a fob, a building pass.</summary>
    AccessDevice = 4,

    Vehicle = 5,
    Other = 6,
}

/// <summary>
/// Where a thing is in its life.
/// </summary>
/// <remarks>
/// <b><see cref="Retired"/> and <see cref="Lost"/> are deliberately different.</b> Retiring is a
/// decision somebody made about a machine that had come to the end of its use; losing one is an
/// incident, and a laptop with the firm's data on it going missing is a different conversation
/// with different people in it. Collapsing them into "gone" would make the second invisible in
/// exactly the record somebody would go looking for it in.
/// </remarks>
public enum AssetStatus
{
    /// <summary>The firm has it and nobody is using it.</summary>
    InStock = 1,

    /// <summary>Somebody has it.</summary>
    Issued = 2,

    /// <summary>Away being fixed.</summary>
    BeingRepaired = 3,

    /// <summary>Its working life is over: sold, scrapped, given away.</summary>
    Retired = 4,

    /// <summary>Nobody knows where it is.</summary>
    Lost = 5,
}

/// <summary>
/// One thing that happened to a thing.
/// </summary>
/// <remarks>
/// Append-only, like an incident's timeline and for the same reason: the value of a lifecycle is
/// that it cannot be tidied up afterwards. "Issued to Amina on 24 September, back on 3 March
/// with a cracked screen, repaired, issued to Brian" is a sentence somebody can act on; a status
/// column on its own is a sentence about today.
/// </remarks>
public sealed class AssetMovement : Entity
{
    private AssetMovement() => What = string.Empty;

    internal AssetMovement(
        AssetStatus to, string what, Guid? personId, DateOnly on, DateTimeOffset at)
    {
        To = to;
        What = string.IsNullOrWhiteSpace(what)
            ? throw new ArgumentException("Say what happened.", nameof(what))
            : what.Trim();
        PersonId = personId;
        On = on;
        RecordedAt = at;
    }

    /// <summary>What it became.</summary>
    public AssetStatus To { get; private init; }

    /// <summary>What happened, in words.</summary>
    public string What { get; private init; }

    /// <summary>Whoever it concerns — the person it went to, or came back from.</summary>
    public Guid? PersonId { get; private init; }

    /// <summary>The day it happened, which is often not the day it was typed.</summary>
    public DateOnly On { get; private init; }

    public DateTimeOffset RecordedAt { get; private init; }
}

/// <summary>
/// Something the firm owns.
/// </summary>
/// <remarks>
/// Section 15, and it exists because of a duplication this codebase created for itself. Section
/// 9 recorded what a leaver had to give back, and section 8 recorded what a joiner was handed —
/// two lists, neither of them able to answer the only question an asset register is for: where
/// is laptop C02XK1JQ, and who has had it. Both of those lists are now views onto this one.
///
/// <b>One row per thing, and the movements are the history.</b> Status says where it is today
/// and the movement log says how it got there, which is the part that settles arguments: whether
/// the screen was already cracked, whether the phone came back at all, how many times this
/// particular machine has been away being fixed before somebody decides to stop repairing it.
///
/// <b>What this is not.</b> There is no depreciation here and no book value — the cost is
/// recorded because somebody will ask what the firm spent on laptops this year, and turning that
/// into a balance sheet entry is section 18's business and is not done. It is also not a
/// procurement system: nothing here raises a purchase order, and section 61 is where that would
/// live.
/// </remarks>
public sealed class Asset : Entity, IAuditable
{
    private readonly List<AssetMovement> _movements = [];

    private Asset()
    {
        Tag = string.Empty;
        Description = string.Empty;
    }

    private Asset(
        string tag,
        AssetKind kind,
        string description,
        string? serialNumber,
        DateOnly boughtOn,
        Money? cost,
        DateTimeOffset at)
    {
        Tag = Required(tag, nameof(tag)).ToUpperInvariant();
        Kind = kind;
        Description = Required(description, nameof(description));
        SerialNumber = Trimmed(serialNumber);
        BoughtOn = boughtOn;
        CostMinorUnits = cost?.MinorUnits;
        CostCurrency = cost?.Currency;
        Status = AssetStatus.InStock;

        _movements.Add(new AssetMovement(
            AssetStatus.InStock, "Bought", null, boughtOn, at));

        Raise(new AssetBought(Id, Tag, Kind, boughtOn));
    }

    public static Asset Buy(
        string tag,
        AssetKind kind,
        string description,
        DateOnly boughtOn,
        DateTimeOffset at,
        string? serialNumber = null,
        Money? cost = null) =>
        new(tag, kind, description, serialNumber, boughtOn, cost, at);

    /// <summary>
    /// The firm's own label for it, upper-cased and unique.
    /// </summary>
    /// <remarks>
    /// Not the serial number, which is the manufacturer's and is sometimes missing, sometimes
    /// unreadable on a sticker, and never the thing written on the box. A tag the firm gives out
    /// is the one identifier that exists for every asset and can be said down a telephone.
    ///
    /// Upper-cased on the way in, because the index is case-sensitive and JD-014 typed as jd-014
    /// would otherwise be a second asset.
    /// </remarks>
    public string Tag { get; private init; }

    public AssetKind Kind { get; private set; }

    /// <summary>Which one. "MacBook Air 13in, 2025" rather than "laptop".</summary>
    public string Description { get; private set; }

    /// <summary>The manufacturer's, when there is one.</summary>
    public string? SerialNumber { get; private set; }

    public DateOnly BoughtOn { get; private set; }

    public long? CostMinorUnits { get; private set; }

    public string? CostCurrency { get; private set; }

    public Money? Cost =>
        CostMinorUnits is { } minor && CostCurrency is { Length: 3 } currency
            ? Money.Of(minor, currency)
            : null;

    /// <summary>
    /// When the warranty runs out, if anybody wrote it down.
    /// </summary>
    /// <remarks>
    /// The one date on an asset that costs money to miss: a machine sent for repair the week
    /// after its warranty lapsed is a bill the firm did not have to pay.
    /// </remarks>
    public DateOnly? WarrantyEndsOn { get; private set; }

    public AssetStatus Status { get; private set; }

    /// <summary>Who has it, when somebody does.</summary>
    public Guid? HeldById { get; private set; }

    public DateOnly? HeldSince { get; private set; }

    public string? Notes { get; private set; }

    /// <summary>
    /// Everything that has happened, most recent first.
    /// </summary>
    /// <remarks>
    /// Returns a copy — see the note on Invoice.Lines for why.
    ///
    /// Three keys, and the third is not decoration. Two movements on the same day, recorded in
    /// the same second, tie on both of the obvious ones — which is the ordinary case for kit
    /// bought and issued in one sitting, and was how a test found this list handing back its
    /// rows in whatever order the database felt like. The identifier breaks it, because a
    /// GUIDv7 sorts by the moment it was created: the reason this codebase uses them.
    /// </remarks>
    public IReadOnlyList<AssetMovement> Movements =>
        [.. _movements
            .OrderByDescending(one => one.On)
            .ThenByDescending(one => one.RecordedAt)
            .ThenByDescending(one => one.Id)];

    public bool IsOut => Status == AssetStatus.Issued;

    /// <summary>Is it still something the firm expects to see again?</summary>
    public bool IsLive => Status is not (AssetStatus.Retired or AssetStatus.Lost);

    public bool WarrantyHasRun(DateOnly on) =>
        WarrantyEndsOn is { } ends && on > ends;

    public void Describe(AssetKind kind, string description, string? serialNumber)
    {
        Kind = kind;
        Description = Required(description, nameof(description));
        SerialNumber = Trimmed(serialNumber);
    }

    /// <summary>Correct the paperwork: what it cost, when it arrived, the warranty, a note.</summary>
    /// <remarks>
    /// One method rather than four, because they are always corrected together — somebody has
    /// the invoice in front of them — and four buttons on one panel is four chances to press the
    /// wrong one.
    /// </remarks>
    public void Paperwork(Money? cost, DateOnly boughtOn, DateOnly? warrantyEndsOn, string? notes)
    {
        CostMinorUnits = cost?.MinorUnits;
        CostCurrency = cost?.Currency;
        BoughtOn = boughtOn;
        WarrantyEndsOn = warrantyEndsOn;
        Notes = Trimmed(notes);
    }

    /// <summary>
    /// Give it to somebody.
    /// </summary>
    /// <remarks>
    /// Refused when somebody already has it, and the refusal names them. Two people holding one
    /// laptop is the state this register exists to make impossible, and the way it happens is
    /// never a decision — it is somebody issuing a machine they did not know was already out.
    /// </remarks>
    public void Issue(Guid personId, DateOnly on, DateTimeOffset at, string? why = null)
    {
        if (!IsLive)
        {
            throw new InvalidOperationException(
                Status == AssetStatus.Retired
                    ? "This has been retired, so it cannot be issued to anybody."
                    : "This is recorded as lost. Find it, or record it as found first.");
        }

        if (Status == AssetStatus.Issued)
        {
            throw new InvalidOperationException(
                "Somebody already has this. Take it back first — two people holding one machine "
                + "is the thing this register is for.");
        }

        Status = AssetStatus.Issued;
        HeldById = personId;
        HeldSince = on;

        _movements.Add(new AssetMovement(
            AssetStatus.Issued, why ?? "Issued", personId, on, at));

        Raise(new AssetIssued(Id, Tag, personId, on));
    }

    /// <summary>
    /// It has come back.
    /// </summary>
    /// <remarks>
    /// The condition is recorded on the movement rather than on the asset, because it is a fact
    /// about a moment and not about the machine — and the argument it settles six months later
    /// is about which moment.
    /// </remarks>
    public void TakeBack(DateOnly on, DateTimeOffset at, string? condition = null)
    {
        if (Status != AssetStatus.Issued)
        {
            throw new InvalidOperationException("Nobody has this, so it cannot come back.");
        }

        var from = HeldById;

        Status = AssetStatus.InStock;
        HeldById = null;
        HeldSince = null;

        _movements.Add(new AssetMovement(
            AssetStatus.InStock,
            condition is { Length: > 0 } said ? $"Returned: {said}" : "Returned",
            from,
            on,
            at));

        Raise(new AssetReturned(Id, Tag, from, on));
    }

    /// <summary>Away being fixed.</summary>
    public void SendForRepair(string why, DateOnly on, DateTimeOffset at)
    {
        if (!IsLive)
        {
            throw new InvalidOperationException("This is no longer in service.");
        }

        var from = HeldById;

        Status = AssetStatus.BeingRepaired;
        HeldById = null;
        HeldSince = null;

        _movements.Add(new AssetMovement(
            AssetStatus.BeingRepaired, Required(why, nameof(why)), from, on, at));
    }

    public void BackFromRepair(string what, DateOnly on, DateTimeOffset at)
    {
        if (Status != AssetStatus.BeingRepaired)
        {
            throw new InvalidOperationException("This is not away being repaired.");
        }

        Status = AssetStatus.InStock;

        _movements.Add(new AssetMovement(
            AssetStatus.InStock, Required(what, nameof(what)), null, on, at));
    }

    /// <summary>
    /// Its working life is over.
    /// </summary>
    /// <remarks>
    /// Kept rather than deleted, for the reason an abandoned pay run is kept: somebody asking
    /// next year what happened to JD-014 finds the answer instead of finding nothing. A deleted
    /// row also takes the movement history with it, which is the part worth having.
    /// </remarks>
    public void Retire(string why, DateOnly on, DateTimeOffset at)
    {
        if (Status == AssetStatus.Retired)
        {
            return;
        }

        Status = AssetStatus.Retired;
        HeldById = null;
        HeldSince = null;

        _movements.Add(new AssetMovement(
            AssetStatus.Retired, Required(why, nameof(why)), null, on, at));

        Raise(new AssetRetired(Id, Tag, on));
    }

    /// <summary>
    /// Nobody knows where it is.
    /// </summary>
    /// <remarks>
    /// Its own state rather than a retirement with a sad reason. Who was holding it is kept on
    /// the movement, because the first question is always who had it last — and because a
    /// machine with the firm's data on it going missing is something somebody has to decide
    /// what to do about, which they cannot do if it reads as scrapped.
    /// </remarks>
    public void Missing(string why, DateOnly on, DateTimeOffset at)
    {
        var from = HeldById;

        Status = AssetStatus.Lost;
        HeldById = null;
        HeldSince = null;

        _movements.Add(new AssetMovement(
            AssetStatus.Lost, Required(why, nameof(why)), from, on, at));

        Raise(new AssetLost(Id, Tag, from, on));
    }

    /// <summary>It turned up.</summary>
    public void Found(DateOnly on, DateTimeOffset at)
    {
        if (Status != AssetStatus.Lost)
        {
            throw new InvalidOperationException("This is not recorded as lost.");
        }

        Status = AssetStatus.InStock;

        _movements.Add(new AssetMovement(AssetStatus.InStock, "Found", null, on, at));
    }

    /// <summary>
    /// The cost stays out of the trail; everything else about a thing is in it.
    /// </summary>
    /// <remarks>
    /// Not because it is sensitive in the way a salary is, but because the trail is append-only
    /// and never pruned and what somebody paid for a laptop is a number the accounts already
    /// hold. That it was bought, issued, lost or retired is the act.
    /// </remarks>
    public static IReadOnlySet<string> AuditExcludes { get; } =
        new HashSet<string> { nameof(CostMinorUnits) };

    private static string Required(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record AssetBought(
    Guid AssetId, string Tag, AssetKind Kind, DateOnly On) : DomainEvent;

public sealed record AssetIssued(
    Guid AssetId, string Tag, Guid PersonId, DateOnly On) : DomainEvent;

public sealed record AssetReturned(
    Guid AssetId, string Tag, Guid? From, DateOnly On) : DomainEvent;

public sealed record AssetRetired(Guid AssetId, string Tag, DateOnly On) : DomainEvent;

/// <summary>
/// Something has gone missing.
/// </summary>
/// <remarks>
/// The one event here anybody should want to act on. Nothing listens to it yet, and that is
/// worth being honest about rather than leaving it to look wired up: a laptop with the firm's
/// data on it going missing ought to raise something, and section 27 now has somewhere for that
/// to go.
/// </remarks>
public sealed record AssetLost(
    Guid AssetId, string Tag, Guid? LastHeldBy, DateOnly On) : DomainEvent;
