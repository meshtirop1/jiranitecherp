using System.Security.Claims;
using JiranisokoTech.Application.Abstractions;

namespace JiranisokoTech.Web.Authorization;

/// <summary>
/// Whoever is making the current request.
/// </summary>
/// <remarks>
/// This is what makes the audit trail name people. Until it was wired up, every
/// entry recorded a null actor — correct, but useless, because "somebody changed
/// the budget" is not an audit trail.
///
/// It returns null outside a request rather than throwing. Background work, the
/// outbox dispatcher and startup all save through the same context, and there
/// is genuinely nobody there; an abstraction that insists on a user would force
/// those callers to invent one, and an entry attributed to a fabricated account
/// is worse than an honest blank.
/// </remarks>
public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid? Id
    {
        get
        {
            var value = Principal?.FindFirstValue(ClaimTypes.NameIdentifier);

            return Guid.TryParse(value, out var id) ? id : null;
        }
    }

    /// <summary>
    /// The display name, read from the principal rather than the database.
    /// </summary>
    /// <remarks>
    /// Deliberately not a lookup. This is read on every save, and a query per
    /// write to fetch a name that is already in the cookie would be a
    /// self-inflicted wound. It is a snapshot, which is exactly what the audit
    /// trail wants anyway: the name as it was when they acted.
    /// </remarks>
    public string? Name =>
        Principal?.FindFirstValue("display_name")
        ?? Principal?.Identity?.Name;

    private ClaimsPrincipal? Principal
    {
        get
        {
            var principal = accessor.HttpContext?.User;

            return principal?.Identity?.IsAuthenticated == true ? principal : null;
        }
    }
}
