namespace JiranisokoTech.Application.People;

/// <summary>
/// Withdrawing somebody's access to this system, from the side that knows about people.
/// </summary>
/// <remarks>
/// Accounts belong to Identity, in infrastructure, and the rules about who may withdraw whose —
/// nobody their own, never the last owner — live there with them. The offboarding checklist
/// needs to cause it, not to reimplement it, so it asks through this.
/// </remarks>
public interface IAccountAccess
{
    /// <summary>
    /// Stop an account signing in, including the session it is in now.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// When the rules refuse it: withdrawing your own access, or the only owner's.
    /// </exception>
    Task WithdrawAsync(
        Guid accountId, Guid? actingAccountId, CancellationToken cancellationToken = default);
}
