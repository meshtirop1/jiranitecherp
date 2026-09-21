using JiranisokoTech.Application.Abstractions;

namespace JiranisokoTech.Infrastructure;

/// <summary>
/// Nobody in particular — the default outside a request.
/// </summary>
/// <remarks>
/// Used by background work, tests and the console. Replaced by an
/// HttpContext-backed implementation once authentication exists, which is the
/// next piece of work.
/// </remarks>
public sealed class UnattributedUser : ICurrentUser
{
    public Guid? Id => null;

    public string? Name => null;
}
