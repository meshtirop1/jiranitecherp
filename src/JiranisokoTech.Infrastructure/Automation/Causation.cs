using JiranisokoTech.Domain.Automation;

namespace JiranisokoTech.Infrastructure.Automation;

/// <summary>What the work in progress is being done because of.</summary>
/// <param name="MessageId">The outbox message being handled, when there is one.</param>
/// <param name="Chain">The rules whose actions led here, oldest first.</param>
/// <param name="Actor">Who to name in the audit trail when nobody is signed in.</param>
public sealed record Cause(Guid? MessageId, IReadOnlyList<Guid> Chain, string? Actor);

/// <summary>
/// The cause of whatever is being saved, carried with the async flow.
/// </summary>
/// <remarks>
/// Ambient rather than passed, because the thing that needs it is at the bottom of every write
/// in the application: <c>AppDbContext</c> stamps each outbox message it writes with the chain
/// of rules that led to it, and names the rule as the actor on each audit entry a rule's action
/// causes. Threading a parameter from the dispatcher through every handler, every service and
/// every repository to reach <c>SaveChangesAsync</c> would touch the whole application to carry
/// one value, and would be forgotten by the next handler written.
///
/// An <see cref="AsyncLocal{T}"/> flows into everything awaited beneath the point it is set
/// and never back out, so a cause set while one message is handled cannot leak into the next
/// message, into a request, or into another dispatcher running beside it.
///
/// <b>The two places that set it.</b> The outbox dispatcher, around each message's handlers,
/// with that message's own chain — so anything a fixed handler writes in reaction carries the
/// chain onward. And the automation run, around its actions, with the chain plus its own rule
/// and the rule's name as the actor.
/// </remarks>
public static class Causation
{
    private static readonly AsyncLocal<Cause?> Ambient = new();

    public static Cause? Current => Ambient.Value;

    /// <summary>The chain to write on an outbox message saved now, or null for none.</summary>
    public static string? Stamp => Current is { } cause ? AutomationRun.Stamp(cause.Chain) : null;

    public static IDisposable Enter(Cause cause)
    {
        var previous = Ambient.Value;

        Ambient.Value = cause;

        return new Restore(previous);
    }

    private sealed class Restore(Cause? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}
