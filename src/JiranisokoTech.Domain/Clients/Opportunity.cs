using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Clients;

/// <summary>
/// Where a piece of possible work has got to.
/// </summary>
/// <remarks>
/// Six stages, and the last two are endings rather than steps. Fewer would not describe how
/// work is actually won here; more would be a form somebody fills in to make a report look
/// busy.
/// </remarks>
public enum Stage
{
    /// <summary>Somebody got in touch, or somebody here thought of them.</summary>
    Enquiry = 1,

    /// <summary>There is a real need and a budget somewhere behind it.</summary>
    Qualified = 2,

    /// <summary>A proposal has gone out.</summary>
    Proposed = 3,

    /// <summary>They want it and the terms are being argued about.</summary>
    Negotiating = 4,

    Won = 5,

    Lost = 6,
}

/// <summary>
/// A piece of work the firm might be paid for.
/// </summary>
/// <remarks>
/// Section 16 had clients, their contracts and their invoices — the whole of the
/// relationship after it has been won, and nothing about winning it. So the pipeline lived
/// in somebody's head and in a spreadsheet, and the two disagreed.
///
/// <b>A lead and an opportunity are one thing at different stages.</b> Modelling them
/// separately is the obvious move and it is wrong: it needs a conversion step, and every
/// conversion step in every system ever built loses the history — the notes, the dates, who
/// first spoke to them. Here an enquiry is simply an opportunity at the first stage, and it
/// keeps everything it accumulated when it moves on.
///
/// <b>A client is optional.</b> Most enquiries are from somebody the firm has never worked
/// for, and demanding a client record first means either a pipeline that cannot hold them
/// or a client list full of people who were never clients. So an opportunity carries a name
/// until there is a client to attach it to.
///
/// <b>Winning one creates nothing.</b> Taking somebody on as a client, raising a project and
/// agreeing a contract are three decisions a person makes with their own consequences and
/// their own permissions. An opportunity that created all three on being marked won would be
/// a dropdown with the authority of a signature.
/// </remarks>
public sealed class Opportunity : Entity, IAuditable
{
    private readonly List<Activity> _activities = [];

    private Opportunity()
    {
        Title = string.Empty;
        About = string.Empty;
    }

    private Opportunity(
        string title,
        string about,
        Guid? clientId,
        Guid? ownerId,
        long? minorUnits,
        string? currency,
        DateOnly? expectedOn,
        DateTimeOffset at)
    {
        Title = Required(title, nameof(title));
        About = Required(about, nameof(about));
        ClientId = clientId;
        OwnerId = ownerId;
        ValueMinorUnits = minorUnits;
        ValueCurrency = currency;
        ExpectedOn = expectedOn;
        Stage = Stage.Enquiry;
        OpenedAt = at;
        MovedAt = at;

        Raise(new OpportunityOpened(Id, Title, About, clientId, at));
    }

    public static Opportunity Open(
        string title,
        string about,
        Guid? clientId = null,
        Guid? ownerId = null,
        long? minorUnits = null,
        string? currency = null,
        DateOnly? expectedOn = null,
        DateTimeOffset at = default) =>
        new(title, about, clientId, ownerId, minorUnits, currency, expectedOn, at);

    /// <summary>What the work is. "Fleet tracking for the northern depots".</summary>
    public string Title { get; private set; }

    /// <summary>
    /// Who it is with, as somebody would say it.
    /// </summary>
    /// <remarks>
    /// Kept even once a client record exists, because it is what was written down at the
    /// time — an enquiry from "Acme Haulage (via Grace)" that becomes a client called
    /// "Acme Haulage Ltd" is the same enquiry, and losing the first spelling loses the only
    /// clue about where it came from.
    /// </remarks>
    public string About { get; private set; }

    /// <summary>The client, once there is one.</summary>
    public Guid? ClientId { get; private set; }

    /// <summary>Whose it is to chase.</summary>
    /// <remarks>
    /// Nullable, because an enquiry that arrives on a Friday belongs to nobody until Monday
    /// — and an opportunity nobody owns is exactly what a pipeline review is for finding.
    /// </remarks>
    public Guid? OwnerId { get; private set; }

    /// <summary>What it might be worth, as minor units beside a currency.</summary>
    /// <remarks>
    /// A guess, and never treated as anything else: nothing in this system adds it to a
    /// figure the firm has actually earned. A pipeline total is a forecast, and putting it
    /// beside revenue on one screen is how a forecast becomes a number somebody spends.
    /// </remarks>
    public long? ValueMinorUnits { get; private set; }

    public string? ValueCurrency { get; private set; }

    public Common.Money? Value =>
        ValueMinorUnits is { } minor && ValueCurrency is { Length: 3 } currency
            ? Common.Money.Of(minor, currency)
            : null;

    /// <summary>When it is expected to be decided, one way or the other.</summary>
    public DateOnly? ExpectedOn { get; private set; }

    public Stage Stage { get; private set; }

    public DateTimeOffset OpenedAt { get; private init; }

    /// <summary>When it last moved, which is how a stalled one is found.</summary>
    public DateTimeOffset MovedAt { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    /// <summary>Why it was lost, which is the only thing a lost one is good for.</summary>
    public string? Outcome { get; private set; }

    /// <remarks>Returns a copy — see the note on Invoice.Lines for why.</remarks>
    public IReadOnlyList<Activity> Activities => _activities.ToList();

    public bool IsOpen => Stage is not (Stage.Won or Stage.Lost);

    /// <summary>
    /// How long since anything happened to it.
    /// </summary>
    /// <remarks>
    /// The one derived figure on this record, and the only thing a pipeline screen needs to
    /// sort by. A list ordered by value shows what somebody hopes for; a list ordered by
    /// silence shows what they have stopped doing.
    /// </remarks>
    public int DaysSinceMoved(DateTimeOffset now) => (int)(now - MovedAt).TotalDays;

    public void Retitle(string title, string about)
    {
        Title = Required(title, nameof(title));
        About = Required(about, nameof(about));
    }

    public void Belongs(Guid? clientId) => ClientId = clientId;

    public void Owned(Guid? ownerId) => OwnerId = ownerId;

    public void Worth(Common.Money? value)
    {
        if (value is { MinorUnits: < 0 })
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), "A piece of work cannot be worth less than nothing.");
        }

        ValueMinorUnits = value?.MinorUnits;
        ValueCurrency = value?.Currency;
    }

    public void ExpectedBy(DateOnly? on) => ExpectedOn = on;

    /// <summary>
    /// Move it along, or back.
    /// </summary>
    /// <remarks>
    /// Backwards is allowed, deliberately. A proposal that comes back for requalification is
    /// an ordinary Tuesday, and a pipeline that only moved one way would be one people worked
    /// around by opening a second opportunity — which then counts twice in every total.
    ///
    /// What is refused is moving one that has already ended. Won and lost are answers, and
    /// reopening one would make the month's figures depend on when somebody last looked.
    /// </remarks>
    public void MoveTo(Stage stage, DateTimeOffset at, string? outcome = null)
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException(
                $"This was already marked {Stage.ToString().ToLowerInvariant()}. Open a new one "
                + "rather than reopening this, so the month it was decided in stays decided.");
        }

        if (stage == Stage.Lost && string.IsNullOrWhiteSpace(outcome))
        {
            /*
             * The one thing a lost opportunity is good for. Without the reason it is a row
             * that says somebody said no, which nobody can act on and nobody reads twice.
             */
            throw new ArgumentException(
                "Say why it was lost. That sentence is the only useful thing a lost "
                + "opportunity leaves behind.",
                nameof(outcome));
        }

        Stage = stage;
        MovedAt = at;

        if (stage is Stage.Won or Stage.Lost)
        {
            ClosedAt = at;
            Outcome = string.IsNullOrWhiteSpace(outcome) ? null : outcome.Trim();

            Raise(new OpportunityClosed(Id, Title, stage, ClientId, ValueMinorUnits,
                ValueCurrency, Outcome, at));

            return;
        }

        Raise(new OpportunityMoved(Id, Title, stage, at));
    }

    /// <summary>
    /// Write down what happened.
    /// </summary>
    /// <remarks>
    /// Recording an activity moves the clock on the opportunity, because a call about it is
    /// something happening to it — and a pipeline that called an opportunity stale while
    /// somebody was speaking to the client weekly would be a report nobody believed twice.
    /// </remarks>
    public void Happened(ActivityKind kind, string what, Guid? byEmployeeId, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(what))
        {
            throw new ArgumentException("Say what happened.", nameof(what));
        }

        _activities.Add(Activity.Of(kind, what, byEmployeeId, at));
        MovedAt = at;
    }

    /// <summary>
    /// The value is kept out of the audit trail.
    /// </summary>
    /// <remarks>
    /// Not because it is secret from the people who hold audit.view, but because it is a
    /// guess that changes weekly — and a trail carrying every revision of every forecast
    /// buries the changes somebody actually needs to find.
    /// </remarks>
    public static IReadOnlySet<string> AuditExcludes { get; } =
        new HashSet<string> { nameof(ValueMinorUnits), nameof(ValueCurrency) };

    private static string Required(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();
}

/// <summary>What sort of thing happened.</summary>
public enum ActivityKind
{
    Note = 1,
    Call = 2,
    Meeting = 3,
    Email = 4,

    /// <summary>Something was sent — a proposal, a quote, a sample.</summary>
    Sent = 5,
}

/// <summary>
/// Something that happened, written down.
/// </summary>
/// <remarks>
/// Append-only. There is no edit and no delete, because the value of a log is that it says
/// what was true when it was written — and one somebody can tidy afterwards is a log nobody
/// can rely on in the conversation it exists for.
/// </remarks>
public sealed class Activity
{
    private Activity() => What = string.Empty;

    internal static Activity Of(
        ActivityKind kind, string what, Guid? byEmployeeId, DateTimeOffset at) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Kind = kind,
            What = what.Trim(),
            ByEmployeeId = byEmployeeId,
            At = at,
        };

    public Guid Id { get; private init; }

    public ActivityKind Kind { get; private init; }

    public string What { get; private init; }

    public Guid? ByEmployeeId { get; private init; }

    public DateTimeOffset At { get; private init; }
}

public sealed record OpportunityOpened(
    Guid OpportunityId,
    string Title,
    string About,
    Guid? ClientId,
    DateTimeOffset At) : DomainEvent;

public sealed record OpportunityMoved(
    Guid OpportunityId, string Title, Stage Stage, DateTimeOffset At) : DomainEvent;

/// <summary>
/// It was won or lost.
/// </summary>
/// <remarks>
/// Carries the value, unlike the audit trail, because this is the event a forecast is built
/// from and the figure is the whole of what it is for. It goes to the outbox and not to a
/// screen full of people.
/// </remarks>
public sealed record OpportunityClosed(
    Guid OpportunityId,
    string Title,
    Stage Stage,
    Guid? ClientId,
    long? ValueMinorUnits,
    string? ValueCurrency,
    string? Outcome,
    DateTimeOffset At) : DomainEvent;
