namespace JiranisokoTech.Domain.Common;

/// <summary>Base for events that are happening now, which is nearly all of them.</summary>
public abstract record DomainEvent : IDomainEvent
{
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}
