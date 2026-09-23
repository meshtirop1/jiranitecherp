using System.Security.Cryptography;
using System.Text;
using JiranisokoTech.Infrastructure.Engineering;

namespace JiranisokoTech.Tests.Engineering;

/// <summary>
/// The signature check, which is the only thing standing at this door.
/// </summary>
/// <remarks>
/// The webhook endpoint is anonymous. There is no cookie, no API key and no
/// person behind a delivery — the signature is the entire authentication story,
/// and every one of these tests is a way of getting past it that must not work.
///
/// Worth being blunt about the stakes: something that could post accepted
/// deliveries here could write commits, pull requests and merges into the
/// firm's record of what was built and by whom. That record is what invoices
/// are defended with.
/// </remarks>
public class GitHubSignatureTests
{
    private const string Secret = "the-secret-github-also-holds";

    private static readonly byte[] Body =
        Encoding.UTF8.GetBytes("""{"action":"opened","number":412}""");

    private readonly GitHubProvider _provider = new();

    [Fact]
    public void A_correctly_signed_body_is_accepted() =>
        Assert.True(_provider.IsSigned(Body, Signed(Body, Secret), Secret));

    [Fact]
    public void A_body_signed_with_a_different_secret_is_refused() =>
        Assert.False(_provider.IsSigned(Body, Signed(Body, "somebody-elses-secret"), Secret));

    /// <summary>
    /// A valid signature over different content does not carry.
    /// </summary>
    /// <remarks>
    /// The attack this actually stops: capturing a real delivery, editing the
    /// payload, and posting it back with the signature that came with it.
    /// Without this the whole scheme would only prove that some delivery was
    /// once signed, not that this one was.
    /// </remarks>
    [Fact]
    public void A_signature_from_a_different_body_is_refused()
    {
        var elsewhere = Encoding.UTF8.GetBytes("""{"action":"closed","number":412}""");

        Assert.False(_provider.IsSigned(Body, Signed(elsewhere, Secret), Secret));
    }

    /// <summary>
    /// Nothing malformed gets through, and nothing malformed throws.
    /// </summary>
    /// <remarks>
    /// Both halves matter. Getting through would be a hole; throwing would mean
    /// any unauthenticated caller could fill the error log and the traces by
    /// sending nonsense, which is its own kind of hole.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256=")]
    [InlineData("nonsense")]
    [InlineData("sha1=0000000000000000000000000000000000000000")]
    [InlineData("sha256=not-hex-at-all-not-hex-at-all-not-hex-at-all-not-hex-at-all-nope")]
    [InlineData("sha256=abcd")]
    [InlineData("sha256=00")]
    public void A_signature_that_is_not_one_is_refused(string? signature) =>
        Assert.False(_provider.IsSigned(Body, signature, Secret));

    /// <summary>
    /// A signature the right length but entirely wrong is refused.
    /// </summary>
    /// <remarks>
    /// Separated from the malformed cases because it takes a different path:
    /// this one parses cleanly and is compared, so it exercises the comparison
    /// rather than the parsing.
    /// </remarks>
    [Fact]
    public void A_signature_of_the_right_shape_and_wrong_value_is_refused() =>
        Assert.False(_provider.IsSigned(Body, "sha256=" + new string('a', 64), Secret));

    /// <summary>
    /// The prefix is required, and not merely tolerated.
    /// </summary>
    /// <remarks>
    /// GitHub always sends it. Accepting a bare hex digest as well would widen
    /// what this endpoint takes for no reason at all, and the widest thing a
    /// verifier accepts is the thing an attacker will send.
    /// </remarks>
    [Fact]
    public void A_bare_digest_without_the_prefix_is_refused()
    {
        var bare = Signed(Body, Secret)["sha256=".Length..];

        Assert.False(_provider.IsSigned(Body, bare, Secret));
    }

    /// <summary>
    /// Case in the hex digits does not matter.
    /// </summary>
    /// <remarks>
    /// GitHub sends lowercase, and this asserts the comparison is over bytes
    /// rather than text — which is what stops the scheme resting on a detail of
    /// the provider's formatting that nothing in their contract promises.
    /// </remarks>
    [Fact]
    public void An_upper_case_digest_is_still_the_same_signature()
    {
        var upper = "sha256=" + Signed(Body, Secret)["sha256=".Length..].ToUpperInvariant();

        Assert.True(_provider.IsSigned(Body, upper, Secret));
    }

    /// <summary>
    /// An empty body signs and verifies like any other.
    /// </summary>
    /// <remarks>
    /// GitHub's ping does not, but nothing guarantees a provider never posts an
    /// empty body, and an off-by-one in the reading would show up here first.
    /// </remarks>
    [Fact]
    public void An_empty_body_verifies_against_its_own_signature() =>
        Assert.True(_provider.IsSigned([], Signed([], Secret), Secret));

    private static string Signed(byte[] body, string secret) =>
        "sha256=" + Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));
}
