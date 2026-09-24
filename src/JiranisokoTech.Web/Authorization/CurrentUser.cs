using System.Security.Claims;
using JiranisokoTech.Application.Abstractions;

namespace JiranisokoTech.Web.Authorization;

/// <summary>
/// Whoever is acting, whether that is a request or an interactive circuit.
/// </summary>
/// <remarks>
/// This replaces <c>HttpCurrentUser</c>, which read <c>IHttpContextAccessor.HttpContext?.User</c>
/// and nothing else. That was correct for every page this application had, because every page
/// was statically rendered — and it would have become quietly wrong the first time one was not.
///
/// <b>The fault it prevents, which would not have failed anything.</b> An interactive component
/// runs inside a circuit, and a circuit has no HTTP request: the accessor returns null, so the
/// old implementation returned a null id and a null name. Every save made from an interactive
/// screen would then have written an audit entry with no actor — the change recorded, the page
/// working, the buttons responding, and the trail saying that somebody unknown changed a
/// budget. <c>HttpCurrentUser</c>'s own remark says an entry with a null actor is "correct, but
/// useless, because 'somebody changed the budget' is not an audit trail", which is exactly what
/// the first interactive page would have started producing.
///
/// <b>Why the circuit's principal is held rather than asked for.</b> <see cref="ICurrentUser"/>
/// is synchronous — it is read inside <c>SaveChangesAsync</c>, on every write — and the obvious
/// implementation, asking <c>AuthenticationStateProvider</c> at that moment, does not merely
/// block: <c>ServerAuthenticationStateProvider</c> throws outright when it is asked outside a
/// Razor component's DI scope. This service is resolved in a great many scopes that are nothing
/// of the kind, and asking there took the application down on startup, in the seeding, before a
/// page had rendered. So <see cref="CircuitUser"/> is filled once when a circuit opens, by a
/// circuit handler that lives in the one scope where the question is legal, and this reads what
/// it holds.
/// </remarks>
public sealed class CurrentUser(IHttpContextAccessor accessor, CircuitUser circuit) : ICurrentUser
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
    /// Deliberately not a lookup. This is read on every save, and a query per write to fetch a
    /// name already carried in the cookie would be a self-inflicted wound. It is a snapshot,
    /// which is what the audit trail wants anyway: the name as it was when they acted.
    /// </remarks>
    public string? Name =>
        Principal?.FindFirstValue("display_name")
        ?? Principal?.Identity?.Name;

    /// <summary>
    /// The request's principal, or the circuit's.
    /// </summary>
    /// <remarks>
    /// The request is asked first and not because it is more likely. During the static
    /// pre-render of an interactive page BOTH are available, and they are the same person; the
    /// ordering only decides which is read on the pass where either would do. The request is
    /// the one that has always worked, so it stays first and the circuit is the fallback, which
    /// keeps every statically rendered page on exactly the path it was on before.
    /// </remarks>
    private ClaimsPrincipal? Principal =>
        Authenticated(accessor.HttpContext?.User) ?? circuit.Principal;

    private static ClaimsPrincipal? Authenticated(ClaimsPrincipal? principal) =>
        principal?.Identity?.IsAuthenticated == true ? principal : null;
}
