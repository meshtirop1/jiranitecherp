using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Audit;

/// <summary>
/// An account was used from a browser and address it has not been used from before.
/// </summary>
/// <remarks>
/// In the domain rather than beside the sign-in code, because the dispatcher routes by
/// type and only looks in this assembly — and because this is a fact about the firm's
/// security rather than a detail of how authentication happens to be implemented.
///
/// Raised on the way in without being written by an entity, which makes it the one event
/// in this system put onto the outbox by hand. Signing in is not a change to a business
/// record, so there is no aggregate to raise it.
///
/// It is a notice, never a block. The same signal is produced by a stolen password and by
/// somebody's new laptop, and refusing the sign-in would lock people out of their own
/// accounts on the day they buy a machine.
/// </remarks>
public sealed record SignedInSomewhereNew(
    Guid UserId,
    string Email,
    string Browser,
    string Address,
    DateTimeOffset At) : DomainEvent;
