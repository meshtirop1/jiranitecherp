using JiranisokoTech.Domain.Engineering;

namespace JiranisokoTech.Application.Engineering;

/// <summary>
/// Everything that differs between one Git host and the next.
/// </summary>
/// <remarks>
/// One implementation per provider, resolved by <see cref="Provider"/>. The
/// inbox does not know which host a delivery came from beyond picking the right
/// one of these, which is what keeps GitHub's particular header names and
/// payload shape out of the code that records a commit.
///
/// All of it is pure: headers and a string in, a decision out. No HTTP client,
/// no database, no clock. That is deliberate — every one of these methods is a
/// place where a real payload can be pasted into a test and the answer checked,
/// and a signature verifier that reaches for the network cannot be tested at
/// all.
/// </remarks>
public interface IGitProvider
{
    GitProvider Provider { get; }

    /// <summary>
    /// Is this body signed with the secret we share with the provider?
    /// </summary>
    /// <remarks>
    /// Takes the raw bytes rather than a decoded string, and that is not
    /// fussiness. The signature is computed over the exact bytes sent; decoding
    /// to a string and re-encoding it normalises line endings and Unicode, and
    /// a body that round-trips imperfectly fails verification for a reason that
    /// looks exactly like an attack.
    ///
    /// Returns false rather than throwing on a missing or malformed signature.
    /// An unsigned request is not an exceptional condition — it is the ordinary
    /// traffic of anything on the public internet.
    /// </remarks>
    bool IsSigned(ReadOnlySpan<byte> body, string? signature, string secret);

    /// <summary>The provider's identifier for this delivery, from the headers.</summary>
    string? DeliveryIdIn(IReadOnlyDictionary<string, string> headers);

    /// <summary>What the provider calls this event, from the headers.</summary>
    string? EventIn(IReadOnlyDictionary<string, string> headers);

    /// <summary>The signature the body should carry, from the headers.</summary>
    string? SignatureIn(IReadOnlyDictionary<string, string> headers);

    /// <summary>
    /// Which repository the payload is about, as owner/name.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Read"/> because it is needed before the event is
    /// understood: a delivery about a repository nobody connected is recorded
    /// and ignored without ever being parsed further, and a delivery for an
    /// unknown event still needs to be filed against the right repository.
    /// </remarks>
    string? RepositoryIn(string payload);

    /// <summary>What the payload is saying.</summary>
    GitEvent Read(string eventName, string payload);
}
