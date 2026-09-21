namespace JiranisokoTech.Domain.Common;

/// <summary>
/// Something the business recognises, with an identity that outlives its values.
/// </summary>
/// <remarks>
/// Identifiers are GUIDv7. They sort by creation time like an auto-increment
/// column, so they index well and do not fragment a B-tree the way a random
/// GUID does — but unlike a sequence they can be generated before the row is
/// saved, which is what lets an entity raise events naming itself inside the
/// same transaction that creates it.
/// </remarks>
public abstract class Entity
{
    private readonly List<IDomainEvent> _events = [];

    protected Entity(Guid id) => Id = id;

    protected Entity() => Id = Guid.CreateVersion7();

    public Guid Id { get; private init; }

    /// <summary>
    /// What this entity has done, waiting to be published.
    /// </summary>
    /// <remarks>
    /// Events are collected rather than dispatched at the moment they happen.
    /// A rule that fires halfway through a unit of work will eventually fire for
    /// something that then fails to save, and an email about a hire that never
    /// happened cannot be unsent. The persistence layer publishes these after
    /// the transaction commits.
    /// </remarks>
    public IReadOnlyCollection<IDomainEvent> Events => _events.AsReadOnly();

    protected void Raise(IDomainEvent domainEvent) => _events.Add(domainEvent);

    public void ClearEvents() => _events.Clear();

    public override bool Equals(object? obj) =>
        obj is Entity other && other.GetType() == GetType() && other.Id == Id;

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}
