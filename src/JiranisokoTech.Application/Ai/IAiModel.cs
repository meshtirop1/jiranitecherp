namespace JiranisokoTech.Application.Ai;

/// <summary>
/// A language model, reached over the network, that can be asked something and can ask back.
/// </summary>
/// <remarks>
/// Sections 36 and 37. An interface in this layer and an HTTP client in infrastructure, for the
/// reason every other outside system here is built that way: the provider is the part that will
/// change, and the rules about what may be sent to it and who may ask must not change with it.
///
/// The shape is deliberately small. A conversation is a list of turns; a reply either finishes,
/// asks for lookups, runs out of room, or is declined. What the provider sends back is carried
/// as <see cref="ModelReply.Raw"/> and handed back verbatim on the next call, because a reply
/// can contain parts this code does not understand and must not rewrite — a model's reasoning
/// is returned in blocks that are only valid if they come back byte for byte.
///
/// The tests talk to a fake of this interface and nothing else. No test may reach the real
/// provider: it costs money, needs a key the suite does not have, and would make a green run
/// depend on somebody else's network.
/// </remarks>
public interface IAiModel
{
    /// <summary>
    /// Whether a key has been configured at all.
    /// </summary>
    /// <remarks>
    /// Checked before anything is gathered or sent, so a copy of this application with no key
    /// says plainly that the feature is off rather than composing a request it will then fail
    /// to send — and so that no screen ever shows an answer invented locally to fill the gap.
    /// </remarks>
    bool IsConfigured { get; }

    /// <summary>The model asked for, written into the usage log beside each exchange.</summary>
    string Name { get; }

    /// <exception cref="AiUnavailableException">
    /// For every way the call can fail, with a sentence a person can act on.
    /// </exception>
    Task<ModelReply> AskAsync(ModelRequest request, CancellationToken cancellationToken = default);
}

/// <param name="Instructions">What the model is and how it must behave. Kept identical between calls so the provider can cache it.</param>
/// <param name="Conversation">Everything said so far, oldest first.</param>
/// <param name="Tools">The lookups it may ask for. Empty for a single answer.</param>
/// <param name="ReplySchema">
/// A JSON schema the reply must match, or null for prose. Used where a page lays the answer out
/// in labelled fields — a reading of a project, a CV summary — so the labelling is decided by
/// this code rather than by whatever headings the model chose to write.
/// </param>
public sealed record ModelRequest(
    string Instructions,
    IReadOnlyList<ModelTurn> Conversation,
    IReadOnlyList<ToolDefinition> Tools,
    string? ReplySchema = null);

/// <summary>One entry in a conversation.</summary>
public abstract record ModelTurn;

/// <summary>What the person, or this application on their behalf, said.</summary>
/// <param name="Enclosures">Documents sent with it — a CV as a PDF, for instance.</param>
public sealed record Asked(string Text, IReadOnlyList<Enclosure>? Enclosures = null) : ModelTurn;

/// <summary>The model's previous reply, exactly as the provider sent it.</summary>
public sealed record Replied(string Raw) : ModelTurn;

/// <summary>The answers to the lookups the previous reply asked for, all together.</summary>
/// <remarks>
/// All of them in one turn rather than one turn each. A provider that is answered one lookup at a
/// time learns to ask for one at a time, and a question needing four lookups then takes four
/// round trips instead of one.
/// </remarks>
public sealed record ToolResults(IReadOnlyList<ToolResult> Results) : ModelTurn;

public sealed record ToolResult(string CallId, string Content, bool IsError);

/// <param name="InputSchema">A JSON schema for the arguments, as text.</param>
public sealed record ToolDefinition(string Name, string Description, string InputSchema);

/// <param name="Input">The arguments the model chose, as JSON text. Parsed, never string-matched.</param>
public sealed record ToolCall(string Id, string Name, string Input);

/// <summary>A document enclosed with a question.</summary>
public sealed record Enclosure(string MediaType, byte[] Data, string Title);

public enum ReplyEnd
{
    /// <summary>It has said what it has to say.</summary>
    Finished = 1,

    /// <summary>It wants lookups run before it can go on.</summary>
    WantsTools = 2,

    /// <summary>It ran out of room part way. What there is may be incomplete.</summary>
    Truncated = 3,

    /// <summary>The provider declined to answer.</summary>
    Declined = 4,
}

/// <param name="Raw">The reply's content as the provider sent it, for handing back unchanged.</param>
/// <param name="ServedBy">The model that actually answered, which is not always the one asked.</param>
public sealed record ModelReply(
    ReplyEnd End,
    string Text,
    IReadOnlyList<ToolCall> Calls,
    string Raw,
    int InputTokens,
    int OutputTokens,
    string ServedBy);

/// <summary>Why a call did not produce a reply.</summary>
public enum AiFailure
{
    /// <summary>No key. The feature is off, and says so.</summary>
    NotConfigured = 1,

    /// <summary>The provider says too many requests; worth trying again in a minute.</summary>
    RateLimited = 2,

    /// <summary>The provider is overloaded; worth trying again shortly.</summary>
    Overloaded = 3,

    /// <summary>The provider failed on its side.</summary>
    ProviderError = 4,

    /// <summary>No reply within the time allowed.</summary>
    TimedOut = 5,

    /// <summary>The provider could not be reached at all.</summary>
    Unreachable = 6,

    /// <summary>The provider refused the request itself — the key, the model, the size. Trying again will not help.</summary>
    Rejected = 7,
}

/// <summary>
/// The model could not be asked, and here is why in words.
/// </summary>
/// <remarks>
/// One exception with a kind rather than a hierarchy, because every caller does the same thing
/// with it — shows the sentence and records the kind — and a page that had to catch six types
/// would sooner or later catch five.
/// </remarks>
public sealed class AiUnavailableException(AiFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public AiFailure Failure { get; } = failure;
}
