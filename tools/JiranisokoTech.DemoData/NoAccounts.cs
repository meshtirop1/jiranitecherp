using JiranisokoTech.Application.People;

namespace JiranisokoTech.DemoData;

/// <summary>
/// Sign-ins, which this tool does not touch.
/// </summary>
/// <remarks>
/// <c>PeopleService</c> takes <c>IAccountAccess</c> so that a leaver's sign-in is withdrawn in the
/// same act as their record being closed, and the real implementation is over ASP.NET Identity's
/// UserManager — which would mean standing the whole identity stack up inside a console tool for
/// one method the seed never calls.
///
/// So it is stubbed, and it throws rather than doing nothing. A stub that silently succeeded would
/// mean the day somebody extends this seed to close a leaver's record, the record closes and the
/// account stays live, and nothing anywhere says so. Throwing makes that the first thing they see.
///
/// The seed creates no accounts at all, deliberately. An employee and a sign-in are separate things
/// in this application, and a demonstration database where ten invented people can all log in is
/// one where somebody eventually does — the owner account is seeded by the application itself from
/// configuration, and that is the only way in.
/// </remarks>
public sealed class NoAccounts : IAccountAccess
{
    public Task WithdrawAsync(
        Guid accountId, Guid? actingAccountId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "The demonstration seed does not create sign-ins, so it cannot withdraw one. If a "
            + "step here now closes somebody's record, give the tool the real IAccountAccess or "
            + "the record will close while the account stays live.");
}
