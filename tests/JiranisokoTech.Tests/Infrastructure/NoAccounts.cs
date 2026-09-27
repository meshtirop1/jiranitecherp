using JiranisokoTech.Application.People;

namespace JiranisokoTech.Tests.Infrastructure;

/// <summary>
/// An account store with nothing in it, for tests of people that are not about sign-in.
/// </summary>
/// <remarks>
/// Withdrawing an account is Identity's business and is tested through the real application in
/// <c>OffboardingAccessTests</c>. A test of departments, managers or leave built over a bare
/// database has no Identity to ask, and no account to withdraw.
/// </remarks>
public sealed class NoAccounts : IAccountAccess
{
    public Task WithdrawAsync(
        Guid accountId, Guid? actingAccountId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
