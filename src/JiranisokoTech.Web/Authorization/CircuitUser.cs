using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace JiranisokoTech.Web.Authorization;

/// <summary>
/// Who is holding an interactive circuit open.
/// </summary>
/// <remarks>
/// A circuit has no HTTP request, so <c>IHttpContextAccessor</c> is empty for the whole life of
/// an interactive page and <see cref="CurrentUser"/> would have nobody to name. This is where
/// the missing half comes from.
///
/// It exists as a held value rather than as a call to <c>AuthenticationStateProvider</c> at the
/// moment of asking, and that is not a preference. <c>ServerAuthenticationStateProvider</c>
/// <b>throws</b> when it is asked outside a Razor component's DI scope — "Do not call
/// GetAuthenticationStateAsync outside of the DI scope for a Razor component" — and
/// <see cref="CurrentUser"/> is resolved in a great many scopes that are nothing of the kind:
/// the startup seeding, every scheduled job, the outbox dispatcher, the webhook inbox. Asking
/// it there took the application down on startup, which is how this shape was arrived at.
///
/// Empty is therefore the correct and common state. Only a circuit fills it.
/// </remarks>
public sealed class CircuitUser
{
    public ClaimsPrincipal? Principal { get; set; }
}

/// <summary>
/// Fills <see cref="CircuitUser"/> when an interactive circuit opens.
/// </summary>
/// <remarks>
/// A circuit handler is resolved from the circuit's own scope, which <i>is</i> a Razor
/// component scope — so this is the one place the authentication state can be asked for
/// safely, and it is asked once rather than on every save.
///
/// It also follows the state afterwards. Signing out elsewhere, or a security stamp rolling,
/// raises <c>AuthenticationStateChanged</c>, and a circuit that kept its opening principal
/// would go on attributing writes to somebody whose access had been withdrawn — which is the
/// same fault as a stale cookie, arriving by a different road.
/// </remarks>
public sealed class CircuitUserHandler(
    CircuitUser holder,
    AuthenticationStateProvider authentication) : CircuitHandler, IDisposable
{
    public override async Task OnCircuitOpenedAsync(
        Circuit circuit, CancellationToken cancellationToken)
    {
        authentication.AuthenticationStateChanged += Changed;

        var state = await authentication.GetAuthenticationStateAsync();

        Remember(state.User);
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        /*
         * Deliberately left alone. A dropped connection is a tunnel or a closed laptop lid, not
         * a sign-out — the circuit and everything in it survive, and clearing the principal here
         * would mean the first save after a reconnection recorded nobody.
         */
        return Task.CompletedTask;
    }

    public void Dispose() => authentication.AuthenticationStateChanged -= Changed;

    private void Changed(Task<AuthenticationState> state)
    {
        if (state.IsCompletedSuccessfully)
        {
            Remember(state.Result.User);
        }
    }

    private void Remember(ClaimsPrincipal? principal) =>
        holder.Principal = principal?.Identity?.IsAuthenticated == true ? principal : null;
}
