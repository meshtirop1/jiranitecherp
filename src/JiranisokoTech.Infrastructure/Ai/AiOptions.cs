namespace JiranisokoTech.Infrastructure.Ai;

/// <summary>How the language model is reached, and how much of it anybody may use.</summary>
public sealed class AiOptions
{
    public const string Section = "Ai";

    /// <summary>
    /// The provider's API key. Blank means the feature is off.
    /// </summary>
    /// <remarks>
    /// Blank by default and that is the point: sending the firm's records to a third party has to
    /// be switched on by somebody who meant it, the same reading the mail transport and the metrics
    /// token already have. With no key every AI screen says it is not configured, and nothing is
    /// composed, sent or invented in its place.
    /// </remarks>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Which model to ask.
    /// </summary>
    /// <remarks>
    /// A setting, because models are retired on the provider's timetable rather than ours and a
    /// retired name is a 404 on every question until somebody changes it.
    /// </remarks>
    public string Model { get; set; } = "claude-opus-5";

    /// <summary>
    /// How hard the model thinks before answering: low, medium, high, xhigh or max.
    /// </summary>
    /// <remarks>
    /// Medium, set explicitly. It is the provider's own default for this model, and naming it
    /// here means a change of model does not silently change what each question costs.
    /// </remarks>
    public string Effort { get; set; } = "medium";

    /// <summary>The most the model may write in one reply, its thinking included.</summary>
    public int MaxTokens { get; set; } = 16000;

    /// <summary>
    /// How long one question may take, retries included, before the page gives up.
    /// </summary>
    /// <remarks>
    /// The page is waiting on a person's form post. Two minutes is long for a page and short for
    /// a model that has looked several things up; past it, the person is told it timed out
    /// rather than left looking at a spinner the browser will abandon anyway.
    /// </remarks>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How many times one person may use any AI feature in a day.
    /// </summary>
    /// <remarks>
    /// Each use is billed to the firm by the token, and a question that makes the model look ten
    /// things up costs more than ten that do not. A daily ceiling per person is the control that
    /// needs no one to watch it; the usage log shows whether it is set sensibly.
    /// </remarks>
    public int DailyLimit { get; set; } = 40;

    /// <summary>
    /// How many rounds of lookups the assistant may ask for before it must answer.
    /// </summary>
    /// <remarks>
    /// A model that keeps asking for one more lookup is a loop the firm pays for by the round.
    /// Six is enough for "why is this project late" — the project, its work, its builds — with
    /// room to spare.
    /// </remarks>
    public int MaxRounds { get; set; } = 6;

    /// <summary>Where the provider answers. Changed only to point at a proxy the firm runs.</summary>
    public string BaseAddress { get; set; } = "https://api.anthropic.com/";
}
