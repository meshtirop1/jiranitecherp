using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.Recruitment;

namespace JiranisokoTech.Application.Recruitment;

/// <summary>
/// Throw the careers list away when an advert goes up or comes down.
/// </summary>
/// <remarks>
/// Section 43. <b>This is a latency improvement and it is never the correctness guarantee.</b>
/// That distinction is the section's most important one, and it is easy to get backwards:
/// eviction on domain events reads as the rigorous option and is the weaker one here, for two
/// reasons that both come from how this codebase dispatches events.
///
/// <b>It runs after the commit, on a poll.</b> Handlers are dispatched through the outbox, which
/// the processor drains on its own interval — so there is always a window, measured in seconds,
/// where the advert has changed and the cached list has not. Anything treating this as immediate
/// would be relying on a promise the outbox does not make.
///
/// <b>And it cannot cover the case that actually happens.</b> An advert with a closing date
/// expires at midnight with no event at all — nothing happened, so nothing was raised. Eviction
/// is blind to a day going by. That is why the lifetime on the cached entry is the guarantee and
/// this is only the thing that makes a deliberate change show up sooner than a minute.
///
/// Idempotent, as a handler must be: deleting a key twice deletes a key.
/// </remarks>
public sealed class ForgetTheOpeningsWhenAnAdvertMoves(ICache cache) :
    IDomainEventHandler<PostingPublished>,
    IDomainEventHandler<PostingClosed>
{
    public Task HandleAsync(PostingPublished raised, CancellationToken cancellationToken = default) =>
        cache.ForgetAsync(CacheKeys.Openings, cancellationToken);

    public Task HandleAsync(PostingClosed raised, CancellationToken cancellationToken = default) =>
        cache.ForgetAsync(CacheKeys.Openings, cancellationToken);
}
