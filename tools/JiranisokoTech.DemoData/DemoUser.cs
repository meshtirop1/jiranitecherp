using JiranisokoTech.Application.Abstractions;

namespace JiranisokoTech.DemoData;

/// <summary>
/// Who the audit trail says wrote all of this.
/// </summary>
/// <remarks>
/// <c>UnattributedUser</c> exists for background work and answers null to both, which is right
/// there: a scheduled job is not a person and inventing one would put a stranger in the trail.
/// A seed is different. It is a deliberate act by whoever ran it, and a year of history with no
/// actor at all is the one thing somebody reading the trail later cannot explain — so this names
/// itself, and the identifier stays null because there is no account behind it.
/// </remarks>
public sealed class DemoUser : ICurrentUser
{
    public Guid? Id => null;

    public string? Name => Marker.Actor;
}
