using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace JiranisokoTech.Infrastructure.Engineering;

/// <summary>
/// Proving a body was signed by somebody holding the shared secret.
/// </summary>
/// <remarks>
/// GitHub and Bitbucket both send a hex HMAC-SHA256 over the raw body behind a
/// `sha256=` prefix, differing only in which header carries it, so the check lives
/// here rather than twice.
///
/// Two details are load-bearing and both are easy to get wrong.
///
/// The comparison is <see cref="CryptographicOperations.FixedTimeEquals"/> rather
/// than string equality. An ordinary comparison returns as soon as two bytes
/// differ, and how long it took leaks how much of the signature was right — which
/// is enough to recover a valid one byte by byte against an endpoint that can be
/// called as often as this one can.
///
/// The hash is computed over the raw request bytes. Anything that decodes the body
/// to a string first and re-encodes it is signing a different sequence of bytes
/// whenever the payload holds a character that does not round-trip, and the
/// failure looks precisely like a forged request.
/// </remarks>
internal static class Hmac
{
    private const string Prefix = "sha256=";

    public static bool Signed(ReadOnlySpan<byte> body, string? signature, string secret)
    {
        if (signature is null || !signature.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var hex = signature.AsSpan(Prefix.Length);

        if (hex.Length != 64)
        {
            return false;
        }

        /*
         * Parsed by hand rather than with Convert.FromHexString, which raises on a
         * stray character. A malformed signature is something any unauthenticated
         * caller can send at will, and an endpoint that throws on request is one
         * anybody can fill the error log with.
         */
        Span<byte> offered = stackalloc byte[32];

        for (var index = 0; index < offered.Length; index++)
        {
            if (!byte.TryParse(
                    hex.Slice(index * 2, 2),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out var parsed))
            {
                return false;
            }

            offered[index] = parsed;
        }

        Span<byte> expected = stackalloc byte[32];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body, expected);

        return CryptographicOperations.FixedTimeEquals(expected, offered);
    }

    /// <summary>
    /// A plain shared secret, compared in constant time.
    /// </summary>
    /// <remarks>
    /// For GitLab and Azure DevOps, which do not sign the body at all — GitLab
    /// sends the secret in a header and Azure DevOps sends credentials in a basic
    /// authorization header.
    ///
    /// This is weaker than a signature and the difference is worth stating
    /// plainly: it proves the caller knows the secret, and nothing whatsoever
    /// about the body. Anything that can alter the request in flight can alter
    /// the payload and the check still passes. It is what those two providers
    /// offer, so it is what they get, and it is a reason to prefer GitHub or
    /// Bitbucket where there is a choice.
    /// </remarks>
    public static bool SecretMatches(string? offered, string secret) =>
        offered is not null
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(offered), Encoding.UTF8.GetBytes(secret));
}
