using JiranisokoTech.Application.People;

namespace JiranisokoTech.Infrastructure.Identity;

/// <summary>
/// <see cref="IAccountAccess"/> over the account administration that already enforces who may
/// withdraw whose access.
/// </summary>
/// <remarks>
/// An acting account that is not known is passed on as an empty identifier, which matches no
/// account, so the "not your own" rule still runs rather than being skipped for a caller who
/// could not be identified.
/// </remarks>
public sealed class AccountAccess(UserAdministration administration) : IAccountAccess
{
    public Task WithdrawAsync(
        Guid accountId, Guid? actingAccountId, CancellationToken cancellationToken = default) =>
        administration.DeactivateAsync(accountId, actingAccountId ?? Guid.Empty);
}
