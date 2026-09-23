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
        IEnumerable<string> events,
        DateTimeOffset at)
    {
        Name = Required(name, nameof(name));
        Endpoint = Https(endpoint);
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
        DateTimeOffset at) =>
        new(name, endpoint, protectedSecret, events, at);

    /// <summary>What this integration is, for whoever finds it in a year.</summary>
    public string Name { get; private set; }

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
    private static string Https(string endpoint)
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
