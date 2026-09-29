namespace JiranisokoTech.Infrastructure.Ai;

/// <summary>Which feature a use of the model was.</summary>
public enum AiFeature
{
    Assistant = 1,
    ProjectReading = 2,
    CvSummary = 3,
    InterviewQuestions = 4,
    Draft = 5,
}

/// <summary>What came of it.</summary>
public enum AiOutcome
{
    Answered = 1,

    /// <summary>The model ran out of room or out of rounds; what it said may be incomplete.</summary>
    Incomplete = 2,

    /// <summary>The provider declined to answer.</summary>
    Declined = 3,

    /// <summary>The provider could not be asked, or failed.</summary>
    Failed = 4,
}

/// <summary>
/// One use of the language model: who, when, which feature, about what, what it looked up.
/// </summary>
/// <remarks>
/// Section 36 says the assistant must never show somebody what they may not see; this is how that
/// is checked afterwards. Every lookup the model asked for is named here, with whether it was
/// refused, so "what did it look at when Wanjiru asked about the payment project" has an answer
/// that does not depend on anybody's memory of a chat window.
///
/// Not the audit trail, for the reason a job run is not: the trail records changes to the firm's
/// records, and a question changes nothing. It is its own table so that the daily limit can be
/// counted from it without reading the trail, and so that the usage page is not a filter over
/// everything else.
///
/// What the model said back is deliberately not kept. An answer is built from records the person
/// could see at the time; storing it would make a second, unaudited copy of those records that
/// outlives the permission that allowed them to be read. The question is kept, because it is the
/// person's own words and the only way to understand what was looked up and why.
/// </remarks>
public sealed class AiExchange
{
    private AiExchange()
    {
        Model = string.Empty;
        Lookups = string.Empty;
    }

    public AiExchange(
        DateTimeOffset at,
        Guid? accountId,
        string? askedBy,
        AiFeature feature,
        Guid? subjectId,
        string? question,
        string lookups,
        AiOutcome outcome,
        string? problem,
        string model,
        int inputTokens,
        int outputTokens,
        TimeSpan took)
    {
        Id = Guid.CreateVersion7();
        At = at;
        AccountId = accountId;
        AskedBy = Clip(askedBy, 200);
        Feature = feature;
        SubjectId = subjectId;
        Question = Clip(question, 2000);
        Lookups = Clip(lookups, 2000) ?? string.Empty;
        Outcome = outcome;
        Problem = Clip(problem, 500);
        Model = Clip(model, 100) ?? string.Empty;
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        Milliseconds = (long)took.TotalMilliseconds;
    }

    public Guid Id { get; private init; }

    public DateTimeOffset At { get; private init; }

    /// <summary>The account that asked. What the daily limit counts by.</summary>
    public Guid? AccountId { get; private init; }

    /// <summary>The name at the time, so the log still reads after the account is gone.</summary>
    public string? AskedBy { get; private init; }

    public AiFeature Feature { get; private init; }

    /// <summary>The project or application it was about, when it was about one.</summary>
    public Guid? SubjectId { get; private init; }

    /// <summary>The assistant's question in the person's words. Empty for the other features.</summary>
    public string? Question { get; private init; }

    /// <summary>
    /// Each lookup the model asked for, in order, with "refused" beside those the person could
    /// not have made themselves.
    /// </summary>
    public string Lookups { get; private init; }

    public AiOutcome Outcome { get; private init; }

    /// <summary>Why it failed or was declined, in the sentence the person was shown.</summary>
    public string? Problem { get; private init; }

    public string Model { get; private init; }

    public int InputTokens { get; private init; }

    public int OutputTokens { get; private init; }

    public long Milliseconds { get; private init; }

    private static string? Clip(string? text, int length) =>
        text is null || text.Length <= length ? text : text[..(length - 1)] + "…";
}
