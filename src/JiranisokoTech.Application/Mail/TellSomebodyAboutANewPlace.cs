using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.Audit;
using Microsoft.Extensions.Logging;

namespace JiranisokoTech.Application.Mail;

/// <summary>
/// Tell somebody their account was used somewhere new.
/// </summary>
/// <remarks>
/// Through the outbox rather than during the sign-in, which is the point of the outbox
/// here. A mail server that is slow or unreachable must not make signing in slow or
/// impossible, and the one moment somebody absolutely has to be able to get in is the
/// moment something has gone wrong with their account.
///
/// Idempotent by being harmless. Running twice sends the notice twice, which is a
/// nuisance and not a fault — and the alternative, a table recording which notices have
/// been sent, would be a second thing to get wrong for a message whose whole purpose is
/// to arrive.
/// </remarks>
public sealed class TellSomebodyAboutANewPlace(
    IMailer mailer,
    ILogger<TellSomebodyAboutANewPlace> logger)
    : IDomainEventHandler<SignedInSomewhereNew>
{
    public async Task HandleAsync(
        SignedInSomewhereNew domainEvent, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(domainEvent.Email))
        {
            // An account with no address cannot be warned. Said out loud rather than
            // swallowed, because it means somebody cannot be told about their own account.
            logger.LogWarning(
                "Account {UserId} was used somewhere new and has no email address, so nobody "
                + "could be told.",
                domainEvent.UserId);

            return;
        }

        await mailer.SendAsync(
            Letters.SignedInSomewhereNew(
                domainEvent.Email,
                /*
                 * Addressed by the part of the address before the at sign rather than by a
                 * looked-up name. This handler has an account and not a staff record, and
                 * a message that says "Hello," with nothing after it reads like a mistake
                 * in exactly the message somebody needs to trust.
                 */
                domainEvent.Email.Split('@')[0],
                domainEvent.Browser,
                domainEvent.Address,
                domainEvent.At),
            cancellationToken);
    }
}
