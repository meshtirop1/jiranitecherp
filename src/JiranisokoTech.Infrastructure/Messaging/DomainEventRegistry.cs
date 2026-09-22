using System.Reflection;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Infrastructure.Messaging;

/// <summary>
/// Turns the name stored on an outbox row back into the type it was written from.
/// </summary>
/// <remarks>
/// The outbox stores a plain type name rather than an assembly-qualified one, so
/// that moving a class between namespaces does not turn the backlog into
/// undeliverable rows. That trade has a cost, and this is where it is paid: two
/// event types with the same short name would be indistinguishable on the way
/// back.
///
/// So the clash is refused at startup rather than discovered at dispatch. A
/// process that will not start is a bad morning; a dispatcher that silently
/// hands a PaymentReceived payload to the wrong PaymentReceived handler is a
/// bad quarter.
/// </remarks>
public sealed class DomainEventRegistry
{
    private readonly IReadOnlyDictionary<string, Type> _byName;

    private DomainEventRegistry(IReadOnlyDictionary<string, Type> byName) => _byName = byName;

    public IReadOnlyCollection<string> KnownNames => (IReadOnlyCollection<string>)_byName.Keys;

    /// <summary>Every event type in these assemblies.</summary>
    public static DomainEventRegistry Build(params Assembly[] assemblies) =>
        From(assemblies
            .Distinct()
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type =>
                type is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false }
                && typeof(IDomainEvent).IsAssignableFrom(type)));

    /// <summary>The same, from a list somebody has already chosen.</summary>
    public static DomainEventRegistry From(IEnumerable<Type> events)
    {
        var found = new Dictionary<string, Type>(StringComparer.Ordinal);
        var clashes = new List<string>();

        foreach (var type in events)
        {
            if (found.TryGetValue(type.Name, out var existing) && existing != type)
            {
                clashes.Add($"{type.Name}: {existing.FullName} and {type.FullName}");
                continue;
            }

            found[type.Name] = type;
        }

        if (clashes.Count > 0)
        {
            throw new InvalidOperationException(
                "Two domain events share a name, so a stored message could not be "
                + "matched to the right one. Rename one of each pair: "
                + string.Join("; ", clashes));
        }

        return new DomainEventRegistry(found);
    }

    /// <summary>
    /// The type for a stored name, or null when the code no longer has one.
    /// </summary>
    /// <remarks>
    /// Null is an answer, not a failure. An event class deleted in a later
    /// release leaves rows behind that nothing can ever rebuild, and the
    /// dispatcher abandons those rather than retrying them until the end of
    /// time.
    /// </remarks>
    public Type? Find(string name) => _byName.GetValueOrDefault(name);
}
