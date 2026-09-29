using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Ai;
using JiranisokoTech.Application.Authorization;
using Microsoft.Extensions.Logging;

namespace JiranisokoTech.Infrastructure.Ai;

/// <summary>A model's judgement on one side of a project, and why.</summary>
public sealed record Judgement(string Area, string Assessment, string Because);

/// <summary>
/// The model's reading of a project. Every field is inference.
/// </summary>
public sealed record Reading(
    string Summary,
    IReadOnlyList<Judgement> Judgements,
    IReadOnlyList<string> Blockers,
    string MainRisk);

public sealed record ReadingResult(AnswerStatus Status, Reading? Reading, string? Problem, int Remaining);

/// <summary>
/// Section 37: a model's reading of a project, laid over the facts and never mixed into them.
/// </summary>
/// <remarks>
/// The brief asks that actual data, calculated metrics and inference be clearly distinguished, and
/// the distinction is kept by construction rather than by wording. The facts come from
/// <see cref="ProjectFacts"/> and are shown whether or not a model is configured. The calculations
/// come from code. Only the reading comes from the model, and it comes back in a fixed shape —
/// a judgement per area with its reason, the blockers, the main risk — so the page can label every
/// field of it as inference instead of hoping the model's own headings say so.
///
/// The model is handed the calculated figures rather than asked to work them out. Counting is
/// what code is for; a model asked to count overdue items will occasionally miscount and state the
/// wrong number with complete confidence, and the page would then show two different counts.
/// </remarks>
public sealed class ProjectReading(
    IAiModel model,
    ProjectFacts facts,
    AiLedger ledger,
    IClock clock,
    ILogger<ProjectReading> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The words the model may use for each area. Fixed, so the page can show them consistently.</summary>
    public static IReadOnlyList<string> Assessments { get; } = ["Healthy", "At risk", "In trouble", "Cannot tell"];

    public const string Instructions =
        """
        You read the recorded state of one software project at Jiranisoko Tech Solutions and give a project manager your assessment of it.

        You are given the project's records as JSON: its work items, open pull requests, recent builds and deployments, hours and money where the person asking may see them, figures already calculated from those records, and a list of what was withheld from this person. Work only from what you are given. Something withheld is unknown, not absent: never treat missing builds or money as good news, and say "Cannot tell" for an area you were not shown.

        Judge four areas — Schedule, Engineering, Deployment, Budget — each as Healthy, At risk, In trouble or Cannot tell, with one or two sentences saying which records led you there. Name records by their references (#12, a pull request number, a build name). Use the calculated figures as given; do not recount.

        Then list what is blocking the work, taken from blocked items and their stated reasons, and name the single biggest risk. Write a two- or three-sentence summary. Everything you write is your reading of the records, and the person will see it labelled as such; be specific rather than reassuring.
        """;

    private const string Schema =
        """
        {"type":"object","additionalProperties":false,"required":["summary","judgements","blockers","main_risk"],
         "properties":{
           "summary":{"type":"string"},
           "judgements":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["area","assessment","because"],
             "properties":{"area":{"type":"string","enum":["Schedule","Engineering","Deployment","Budget"]},
                           "assessment":{"type":"string","enum":["Healthy","At risk","In trouble","Cannot tell"]},
                           "because":{"type":"string"}}}},
           "blockers":{"type":"array","items":{"type":"string"}},
           "main_risk":{"type":"string"}}}
        """;

    /// <remarks>
    /// Takes the project's identifier and gathers the facts itself, as the person asking, rather
    /// than accepting facts somebody else gathered. A reading handed a picture could be handed one
    /// gathered for a person who may see more, and the reach check would be somebody else's job.
    /// </remarks>
    public async Task<ReadingResult> ReadAsync(
        Guid projectId, Asker asker, CancellationToken cancellationToken = default)
    {
        if (!model.IsConfigured)
        {
            return new(AnswerStatus.NotConfigured, null,
                "A reading needs the AI provider, and no API key has been set on this copy of the system.", 0);
        }

        if (!asker.Holds(Permissions.AiAnalyse))
        {
            return new(AnswerStatus.NotPermitted, null, $"You do not hold {Permissions.AiAnalyse}.", 0);
        }

        if (await facts.GatherAsync(projectId, asker, cancellationToken) is not { } picture)
        {
            return new(AnswerStatus.NotPermitted, null, "No project you can see has that identifier.", 0);
        }

        var remaining = await ledger.RemainingTodayAsync(asker.AccountId, cancellationToken);

        if (remaining <= 0)
        {
            return new(AnswerStatus.LimitReached, null,
                $"You have used today's {ledger.DailyLimit} uses. The allowance renews at midnight UTC.", 0);
        }

        var timer = Stopwatch.StartNew();
        var records = JsonSerializer.Serialize(new
        {
            Project = new
            {
                picture.Project.Name,
                picture.Project.Code,
                picture.Project.Status,
                picture.Project.DueOn,
                Lead = picture.Project.LeadName,
            },
            Work = picture.Work.Take(ProjectFacts.MostWorkSent),
            picture.OpenPullRequests,
            picture.Builds,
            picture.Deployments,
            picture.Hours,
            picture.Money,
            Calculated = picture.Calculations(),
            picture.Withheld,
        }, Json);

        Reading? reading = null;
        var status = AnswerStatus.Failed;
        string? problem = null;
        ModelReply? reply = null;

        try
        {
            reply = await model.AskAsync(
                new ModelRequest(
                    Instructions,
                    [new Asked($"Today is {clock.Today:yyyy-MM-dd}. The records:\n\n{records}")],
                    [],
                    Schema),
                cancellationToken);

            switch (reply.End)
            {
                case ReplyEnd.Declined:
                    status = AnswerStatus.Declined;
                    problem = "The AI provider declined to read this project.";
                    break;

                case ReplyEnd.Truncated:
                    status = AnswerStatus.Incomplete;
                    problem = "The reading ran out of room before it finished. Try again.";
                    break;

                default:
                    reading = Parse(reply.Text);
                    status = reading is null ? AnswerStatus.Failed : AnswerStatus.Answered;
                    problem = reading is null
                        ? "The reading came back in a shape this page cannot lay out, so none of it is shown."
                        : null;
                    break;
            }
        }
        catch (AiUnavailableException exception)
        {
            problem = exception.Message;
        }

        await RecordAsync(asker, AiFeature.ProjectReading, picture.Project.Id, status, problem, reply, timer.Elapsed, cancellationToken);

        return new ReadingResult(status, reading, problem, Math.Max(0, remaining - 1));
    }

    /// <summary>
    /// The reading, or null when it is not in the shape asked for.
    /// </summary>
    /// <remarks>
    /// Null rather than a best effort. A reading with its judgements missing would be laid out as
    /// though the model had nothing to say about the schedule, which is a different statement from
    /// "this could not be read".
    /// </remarks>
    internal static Reading? Parse(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            var judgements = root.GetProperty("judgements").EnumerateArray()
                .Select(one => new Judgement(
                    one.GetProperty("area").GetString() ?? string.Empty,
                    one.GetProperty("assessment").GetString() ?? string.Empty,
                    one.GetProperty("because").GetString() ?? string.Empty))
                .Where(one => Assessments.Contains(one.Assessment))
                .ToList();

            return new Reading(
                root.GetProperty("summary").GetString() ?? string.Empty,
                judgements,
                [.. root.GetProperty("blockers").EnumerateArray().Select(one => one.GetString() ?? string.Empty)],
                root.GetProperty("main_risk").GetString() ?? string.Empty);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task RecordAsync(
        Asker asker, AiFeature feature, Guid subject, AnswerStatus status, string? problem,
        ModelReply? reply, TimeSpan took, CancellationToken cancellationToken)
    {
        try
        {
            await ledger.RecordAsync(new AiExchange(
                clock.Now, asker.AccountId, asker.Name, feature, subject, null, string.Empty,
                Outcome(status), problem, model.Name, reply?.InputTokens ?? 0, reply?.OutputTokens ?? 0, took),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "A project reading could not be written to the usage log.");
        }
    }

    internal static AiOutcome Outcome(AnswerStatus status) => status switch
    {
        AnswerStatus.Answered => AiOutcome.Answered,
        AnswerStatus.Declined => AiOutcome.Declined,
        AnswerStatus.Incomplete => AiOutcome.Incomplete,
        _ => AiOutcome.Failed,
    };
}
