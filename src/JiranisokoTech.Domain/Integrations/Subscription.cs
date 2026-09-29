using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Integrations;

/// <summary>
/// Somewhere outside this system that wants to be told when something happens.
/// </summary>
/// <remarks>
/// The other half of section 40. Deliveries arrive from Git hosts and are believed
/// because they are signed; this is the same arrangement pointing outwards, and it
/// is the more dangerous direction. An incoming webhook can at worst write nonsense
/// into a table somebody can look at. An outgoing one sends the firm's own data to
/// a third party, and a URL typed wrong is a private disclosure that nothing here
/// can take back.
///
/// Three things follow from that, and all three are in this file rather than in a
/// screen's validation:
///
/// The endpoint must be https, because the payload describes invoices, salaries and
/// clients and plain http puts all of it on the wire.
///
/// A subscription names the events it wants. There is no "everything" — an
/// integration built to watch invoices should not silently begin receiving
/// employee records the day somebody adds a new event type.
///
/// A subscription that keeps failing disables itself. An endpoint that has been
/// gone for a week is either decommissioned or was never right, and a queue
/// retrying into it forever is a queue nobody reads and a slow leak of attempts at
/// somebody else's address.
/// </remarks>
/// <summary>
/// What is at the far end, and therefore what shape to post.
/// </summary>
/// <remarks>
/// Section 51 asks that new providers can be added without changing core business logic, and this
/// is where that sentence is tested. Everything that decides <em>whether</em> to notify somebody —
/// the domain events, the offered list, the queue, the retries, the backoff, the dead letters —
/// stays exactly as it was. What a provider changes is one thing: the bytes on the wire. So a
/// provider is a value on this enum and an <c>IOutboundSender</c> that says it handles it, and
/// nothing above the sender has to know the difference.
///
/// It is stored on the subscription rather than guessed from the endpoint's host. Guessing works
/// for Slack today and fails the first time somebody puts a proxy in front of it, and the failure
/// is silent: the firm's own JSON posted at a chat service, which answers 400 and looks like an
/// endpoint that is down.
/// </remarks>
public enum DestinationKind
{
    /// <summary>
    /// Somebody's own server, told in this system's own envelope.
    /// </summary>
    /// <remarks>
    /// The original and the default, which is why it is 1: every row that existed before this
    /// column did is one of these, and a migration that had to decide would have had to guess.
    /// </remarks>
    Webhook = 1,

    /// <summary>
    /// A Slack channel, told in a sentence.
    /// </summary>
    /// <remarks>
    /// <b>The reason this exists is that the outbound half of section 40 had no possible
    /// consumer.</b> It was finished and tested — signing, retries, backoff, dead letters, a
    /// replay button — and what it posts is a bespoke envelope that only code somebody writes can
    /// read. A firm of twenty software engineers has not written that code and never will, so
    /// every one of those parts was machinery with nothing at the far end.
    ///
    /// A Slack incoming webhook is an https URL somebody pastes, which is the shape this table
    /// already stores and already validates. What was missing was the sentence.
    /// </remarks>
    Slack = 2,
}

public sealed class Subscription : Entity, IAuditable
{
    /// <summary>
    /// How many failed deliveries in a row before a subscription switches itself off.
    /// </summary>
    /// <remarks>
    /// Counted in deliveries that gave up, not in attempts — each of those has
    /// already been retried several times over some hours, so ten of them is a
    /// endpoint that has been unreachable for days rather than one that blinked.
    /// </remarks>
    public const int MaximumConsecutiveFailures = 10;

    /// <summary>
    /// Where a Slack incoming webhook lives.
    /// </summary>
    /// <remarks>
    /// Written here rather than configurable, because it is the one thing about a Slack
    /// destination that is not somebody's choice. A setting for it would be a setting whose only
    /// use is to let somebody send the firm's notifications to an address that is not Slack while
    /// the record says Slack.
    /// </remarks>
    public const string SlackHost = "hooks.slack.com";

    private readonly List<SubscribedEvent> _events = [];

    private Subscription()
    {
        Name = string.Empty;
        Endpoint = string.Empty;
        ProtectedSecret = string.Empty;
    }

    private Subscription(
        string name,
        string endpoint,
        string protectedSecret,
        DestinationKind kind,
        IEnumerable<string> events,
        DateTimeOffset at)
    {
        Name = Required(name, nameof(name));
        Kind = kind;
        Endpoint = Https(endpoint, kind);
        ProtectedSecret = Required(protectedSecret, nameof(protectedSecret));
        CreatedAt = at;

        foreach (var wanted in events.Select(one => one.Trim()).Where(one => one.Length > 0)
                     .Distinct(StringComparer.Ordinal))
        {
            _events.Add(SubscribedEvent.Of(wanted));
        }

        if (_events.Count == 0)
        {
            throw new ArgumentException(
                "A subscription has to name at least one event. One that named none would "
                + "either send nothing or send everything, and neither is something somebody "
                + "chose.",
                nameof(events));
        }

        Raise(new SubscriptionAdded(Id, Name, Endpoint, at));
    }

    public static Subscription Add(
        string name,
        string endpoint,
        string protectedSecret,
        IEnumerable<string> events,
        DateTimeOffset at,
        DestinationKind kind = DestinationKind.Webhook) =>
        new(name, endpoint, protectedSecret, kind, events, at);

    /// <summary>What this integration is, for whoever finds it in a year.</summary>
    public string Name { get; private set; }

    /// <summary>What is at the far end. Fixed, like the endpoint.</summary>
    /// <remarks>
    /// Not changeable, for the same reason the endpoint is not: a subscription whose kind could be
    /// edited is one where somebody repoints a Slack destination at a colleague's server and the
    /// audit trail shows a rename. Adding a second subscription and switching the first off says
    /// what happened.
    /// </remarks>
    public DestinationKind Kind { get; private init; }

    public string Endpoint { get; private init; }

    /// <summary>
    /// The signing secret, encrypted rather than hashed.
    /// </summary>
    /// <remarks>
    /// The one place this system stores a recoverable secret, and the reason is
    /// unavoidable: the payload has to be signed on the way out, and a signature
    /// cannot be computed from a hash. So it is protected with the application's
    /// own key ring — which lives on a volume outside the container — and a copy of
    /// the database alone does not yield it.
    ///
    /// That is a weaker guarantee than the hash kept for an incoming secret, and it
    /// is the weakest link in this feature. It is worth knowing which one it is.
    /// </remarks>
    public string ProtectedSecret { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset? DisabledAt { get; private set; }

    /// <summary>Why it was switched off, whether by a person or by itself.</summary>
    public string? DisabledReason { get; private set; }

    public DateTimeOffset? LastDeliveryAt { get; private set; }

    public int ConsecutiveFailures { get; private set; }

    public bool IsActive => DisabledAt is null;

    /// <summary>
    /// The events this subscription asked for.
    /// </summary>
    /// <remarks>
    /// Returns a copy — see the note on Invoice.Lines for why.
    ///
    /// Called Wanted and not Events, which is what it was, because that name hid
    /// <see cref="Entity.Events"/> — the domain events waiting to go to the outbox.
    /// Nothing was broken by it yet and the next thing written here would have been:
    /// this codebase's convention for testing that something was announced is
    /// <c>Assert.Single(thing.Events.OfType&lt;SomethingHappened&gt;())</c>, and on
    /// this type that would have compiled against the wrong list and found nothing,
    /// for ever, without failing. A test that cannot fail is worse than no test.
    /// </remarks>
    public IReadOnlyList<SubscribedEvent> Wanted => _events.ToList();

    public bool Wants(string eventName) =>
        IsActive && _events.Any(one => one.Name == eventName);

    public void Rename(string name) => Name = Required(name, nameof(name));

    /// <summary>A delivery to this subscription succeeded.</summary>
    /// <remarks>
    /// Resets the failure count, so an endpoint that is flaky rather than gone is
    /// never disabled: the count means "in a row", and one success says the run is
    /// over.
    /// </remarks>
    public void Delivered(DateTimeOffset at)
    {
        LastDeliveryAt = at;
        ConsecutiveFailures = 0;
    }

    /// <summary>A delivery to this subscription gave up.</summary>
    public void Failed(DateTimeOffset at)
    {
        ConsecutiveFailures++;

        if (ConsecutiveFailures >= MaximumConsecutiveFailures && IsActive)
        {
            Disable(
                $"Switched off by the system after {ConsecutiveFailures} deliveries in a row "
                + "gave up. Fix the endpoint and switch it back on.",
                at);
        }
    }

    public void Disable(string reason, DateTimeOffset at)
    {
        if (!IsActive)
        {
            return;
        }

        DisabledAt = at;
        DisabledReason = Required(reason, nameof(reason));

        Raise(new SubscriptionDisabled(Id, Name, Endpoint, DisabledReason, at));
    }

    /// <summary>
    /// Switch it back on.
    /// </summary>
    /// <remarks>
    /// Clears the failure count, because leaving it would mean a subscription
    /// disabled once was one delivery away from disabling again — and somebody who
    /// has just fixed an endpoint would reasonably expect a fresh start.
    /// </remarks>
    public void Enable(DateTimeOffset at)
    {
        if (IsActive)
        {
            return;
        }

        DisabledAt = null;
        DisabledReason = null;
        ConsecutiveFailures = 0;

        Raise(new SubscriptionEnabled(Id, Name, Endpoint, at));
    }

    /// <summary>
    /// The endpoint and the protected secret are both kept out of the trail.
    /// </summary>
    /// <remarks>
    /// The secret for the obvious reason. The endpoint because it is the one field
    /// worth altering — a URL quietly changed is data going somewhere else — and the
    /// trail records it in full on the row that added it, which is where anybody
    /// asking would look. What must not happen is a second copy of it in a table
    /// with different retention.
    /// </remarks>
    public static IReadOnlySet<string> AuditExcludes { get; } =
        new HashSet<string> { nameof(ProtectedSecret) };

    /// <summary>
    /// Refuse anything that is not an absolute https address.
    /// </summary>
    /// <remarks>
    /// In the domain rather than in the form, because this is not a typing
    /// convenience. The payload describes invoices, salaries and clients; plain http
    /// publishes all of it to every hop in between, and a subscription is added once
    /// and then forgotten about for years.
    /// </remarks>
    private static string Https(string endpoint, DestinationKind kind)
    {
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var address))
        {
            throw new ArgumentException(
                "That is not a complete web address.", nameof(endpoint));
        }

        if (address.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException(
                "An outgoing webhook has to be https. The payload describes invoices, pay and "
                + "clients, and plain http shows all of it to everything between here and "
                + "there.",
                nameof(endpoint));
        }

        /*
         * A Slack destination has to be a Slack address, and this is the one place that can
         * insist on it. The kind decides what is posted, so a row marked Slack pointing anywhere
         * else sends chat-shaped JSON at a stranger — which the receiver refuses with a 4xx that
         * looks exactly like an endpoint that is temporarily down, so the queue retries it into
         * somebody else's server for days before disabling itself.
         *
         * The host and not a prefix on the whole URL, because the path carries the credential and
         * Slack has changed its shape before. What must not change is whose server it is.
         */
        if (kind == DestinationKind.Slack && address.Host != SlackHost)
        {
            throw new ArgumentException(
                $"A Slack destination has to be a Slack incoming-webhook address on {SlackHost}. "
                + "Marking somebody else's server as Slack would post chat messages at it, which "
                + "it refuses in a way that looks like an outage.",
                nameof(endpoint));
        }

        return address.ToString();
    }

    private static string Required(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();
}

/// <summary>One event a subscription asked for.</summary>
public sealed class SubscribedEvent
{
    private SubscribedEvent() => Name = string.Empty;

    internal static SubscribedEvent Of(string name) =>
        new() { Id = Guid.CreateVersion7(), Name = name };

    public Guid Id { get; private init; }

    public string Name { get; private init; }
}

public sealed record SubscriptionAdded(
    Guid SubscriptionId, string Name, string Endpoint, DateTimeOffset At) : DomainEvent;

public sealed record SubscriptionDisabled(
    Guid SubscriptionId,
    string Name,
    string Endpoint,
    string Reason,
    DateTimeOffset At) : DomainEvent;

public sealed record SubscriptionEnabled(
    Guid SubscriptionId, string Name, string Endpoint, DateTimeOffset At) : DomainEvent;
