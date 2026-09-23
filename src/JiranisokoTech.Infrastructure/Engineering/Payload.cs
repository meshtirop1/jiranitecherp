using System.Text.Json;

namespace JiranisokoTech.Infrastructure.Engineering;

/// <summary>
/// Reading fields out of somebody else's JSON without trusting any of it.
/// </summary>
/// <remarks>
/// Shared by all four provider adapters. Extracted the moment there was a second
/// one, because four copies of "walk a path and return null if anything along it
/// is missing" would drift — and the way they would drift is that one of them
/// would start throwing on an absent field, which turns a provider adding an
/// optional key into a dead-lettered delivery.
///
/// Every reader here answers "absent" rather than raising, with one deliberate
/// exception: <see cref="Number"/>, because an event identified by a number that
/// is not there cannot be recorded at all, and failing loudly puts the body on the
/// deliveries screen where somebody can look at it.
/// </remarks>
internal static class Payload
{
    /// <summary>A string at the end of a path, or nothing.</summary>
    public static string? Text(JsonElement element, params string[] path) =>
        At(element, path) is { ValueKind: JsonValueKind.String } found
            ? found.GetString()
            : null;

    /// <summary>
    /// A number at the end of a path, however the provider wrote it.
    /// </summary>
    /// <remarks>
    /// Accepts a number written as a string, which is not pedantry: Bitbucket
    /// sends pull request identifiers as numbers and Azure DevOps has sent them
    /// both ways across API versions. Rejecting a quoted number would mean an
    /// integration that worked until the day the provider changed its mind.
    /// </remarks>
    public static int Number(JsonElement element, params string[] path)
    {
        if (At(element, path) is { } found)
        {
            if (found.ValueKind == JsonValueKind.Number && found.TryGetInt32(out var number))
            {
                return number;
            }

            if (found.ValueKind == JsonValueKind.String
                && int.TryParse(found.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        throw new InvalidOperationException(
            $"The payload has no number at '{string.Join('.', path)}', and this event cannot "
            + "be recorded without one.");
    }

    /// <summary>
    /// An identifier at the end of a path, as text, however the provider wrote it.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Number"/> and not a convenience. GitHub's workflow
    /// run and deployment identifiers are past eleven digits and still climbing;
    /// <see cref="Number"/> returns an <c>int</c>, so reading one through it fails
    /// <c>TryGetInt32</c>, falls through, and throws — which would have
    /// dead-lettered every single build delivery, on every repository, with a
    /// message about a missing number that was sitting right there in the payload.
    ///
    /// Returns text rather than a long because that is what the identifier is used
    /// as: an opaque key for "the same run, reported again". Azure DevOps sends a
    /// GUID for the same purpose, so a numeric type could not hold all four hosts
    /// anyway.
    /// </remarks>
    public static string? Identifier(JsonElement element, params string[] path) =>
        At(element, path) switch
        {
            { ValueKind: JsonValueKind.String } found => found.GetString(),
            { ValueKind: JsonValueKind.Number } found => found.GetRawText(),
            _ => null,
        };

    /// <summary>
    /// A timestamp at the end of a path, or now.
    /// </summary>
    /// <remarks>
    /// The provider's own time is preferred because it is when the thing actually
    /// happened, and a delivery replayed a week later would otherwise claim every
    /// commit in it was made the day somebody pressed the button. Falls back to
    /// now when the field is absent or unparseable, because a commit recorded with
    /// a slightly wrong timestamp is far better than one not recorded at all.
    /// </remarks>
    public static DateTimeOffset When(JsonElement element, params string[] path) =>
        Text(element, path) is { } written
        && DateTimeOffset.TryParse(written, out var at)
            ? at
            : DateTimeOffset.UtcNow;

    /// <summary>An array at the end of a path, or an empty one.</summary>
    public static IEnumerable<JsonElement> Each(JsonElement element, params string[] path) =>
        At(element, path) is { ValueKind: JsonValueKind.Array } found
            ? found.EnumerateArray()
            : [];

    /// <summary>Is the value at the end of a path the boolean true?</summary>
    public static bool Flag(JsonElement element, params string[] path) =>
        At(element, path) is { ValueKind: JsonValueKind.True };

    /// <summary>
    /// The first line of a commit message, capped to the column it is stored in.
    /// </summary>
    /// <remarks>
    /// The rest is the body, which is where people paste stack traces, and a board
    /// column is not the place for one. The whole message is still in the stored
    /// payload and in the repository.
    ///
    /// The cap is not decoration: nothing enforces a subject length on the far
    /// side, and a first line running past a thousand characters would fail to
    /// save and take every other commit in the same delivery down with it.
    /// </remarks>
    public static string FirstLine(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "(no message)";
        }

        var end = message.IndexOfAny(['\r', '\n']);
        var line = (end < 0 ? message : message[..end]).Trim();

        if (line.Length > 1000)
        {
            line = line[..1000];
        }

        return line.Length > 0 ? line : "(no message)";
    }

    /// <summary>The branch a ref names, or nothing if it is not a branch.</summary>
    /// <remarks>
    /// Shared because all four providers send refs/heads/… somewhere, and because
    /// the negative case matters as much as the positive one: a tag push carries
    /// commits already recorded against the branch they were made on, so treating
    /// refs/tags/v1.4.0 as a branch would file the same work twice under a name
    /// nobody typed.
    /// </remarks>
    public static string? Branch(string? reference) =>
        reference is not null && reference.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? reference["refs/heads/".Length..]
            : null;

    private static JsonElement? At(JsonElement element, string[] path)
    {
        var current = element;

        foreach (var step in path)
        {
            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(step, out current))
            {
                return null;
            }
        }

        return current;
    }
}
