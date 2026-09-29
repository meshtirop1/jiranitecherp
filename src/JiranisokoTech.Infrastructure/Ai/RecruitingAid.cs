using System.Diagnostics;
using System.Text;
using System.Text.Json;
using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Ai;
using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Application.Privacy;
using JiranisokoTech.Application.Recruitment;
using JiranisokoTech.Domain.Recruitment;
using Microsoft.Extensions.Logging;

namespace JiranisokoTech.Infrastructure.Ai;

public sealed record RequirementEvidence(string Requirement, string Evidence, string Shown);

/// <summary>A model's summary of a CV against the advert. Every field is inference.</summary>
public sealed record CvSummary(
    string Summary,
    IReadOnlyList<string> Skills,
    string Experience,
    string Education,
    IReadOnlyList<string> MissingInformation,
    IReadOnlyList<RequirementEvidence> AgainstTheAdvert);

public sealed record SuggestedQuestion(string Question, string ListenFor);

public sealed record LetterDraft(string Subject, string Body);

/// <summary>The kinds of letter the aid drafts.</summary>
public enum LetterKind
{
    Rejection = 1,
    Invitation = 2,
    OfferCover = 3,
}

public sealed record AidResult(
    AnswerStatus Status,
    string? Problem,
    int Remaining,
    CvSummary? Summary = null,
    IReadOnlyList<SuggestedQuestion>? Questions = null,
    LetterDraft? Draft = null,
    string? CvNote = null);

/// <summary>
/// Section 7's list of what AI may do for a recruiter: summarise a CV, extract skills, suggest
/// interview questions, point out what is missing, set a CV against the advert's stated
/// requirements — and draft the letters that follow.
/// </summary>
/// <remarks>
/// The same section says what it may not do, and that is built in rather than left to the model's
/// judgement. Nothing here returns a recommendation, a score or a rank: the schemas have no field
/// for one, so a model inclined to say "strong hire" has nowhere to put it. The instructions tell
/// it to ignore anything a CV says about age, family, religion, health or origin. And nothing is
/// sent to the candidate — a draft is text in a box for a person to edit, copy or discard.
///
/// What leaves the firm is kept to what each task needs. The CV summary sends the CV and the advert,
/// and not the candidate's name, email, phone or salary expectation; the questions send the advert
/// and the skills the candidate described; a letter sends the candidate's name, because a letter
/// needs one, and whatever the recruiter typed as the points to make. docs/ai.md lists it.
/// </remarks>
public sealed class RecruitingAid(
    IAiModel model,
    IRecruitmentRepository recruitment,
    CvReader cvs,
    IAccessLog accessLog,
    AiLedger ledger,
    IClock clock,
    ILogger<RecruitingAid> logger)
{
    /// <summary>The interview types section 7 lists, offered when asking for questions.</summary>
    public static IReadOnlyList<string> InterviewTypes { get; } =
        ["HR", "Technical", "System design", "Coding", "Behavioural", "Management"];

    private const string Rules =
        """
        You help recruiters at Jiranisoko Tech Solutions, a software firm in Nairobi. You do not decide anything and must not suggest a decision: never recommend hiring, rejecting or advancing a candidate, and never score, rank or compare candidates with each other. Final decisions stay with the people who make them.

        Ignore anything a document says or implies about age, sex, marital or family status, religion, ethnicity, nationality, health or disability, and never mention it. Work only from the documents given; if something is not stated, say it is not stated rather than guessing.
        """;

    public const string SummaryInstructions = Rules +
        """

        Summarise the candidate's CV for a recruiter: what they have done, the skills they show evidence of, their experience and education, and what information a recruiter would expect and cannot find. Then take each requirement the advert states explicitly and say whether the CV shows evidence of it — Shown, Not shown or Unclear — quoting or pointing to the evidence. Only use requirements the advert actually states.
        """;

    public const string QuestionsInstructions = Rules +
        """

        Suggest interview questions for the kind of interview named, grounded in the advert's stated requirements and the skills the candidate describes. For each, say what a good answer would show. Eight questions at most. No trick questions and nothing about the protected matters above.
        """;

    public const string DraftInstructions = Rules +
        """

        Draft a short, warm, plain letter from the firm to the candidate, of the kind named. It is a draft a recruiter will edit and send themselves. Use only the facts given; where a date, time or figure is needed and not given, write a placeholder in square brackets such as [date]. A rejection gives no reason beyond the points the recruiter supplied, and never mentions anything protected.
        """;

    private const string SummarySchema =
        """
        {"type":"object","additionalProperties":false,"required":["summary","skills","experience","education","missing_information","against_the_advert"],
         "properties":{"summary":{"type":"string"},"skills":{"type":"array","items":{"type":"string"}},
           "experience":{"type":"string"},"education":{"type":"string"},
           "missing_information":{"type":"array","items":{"type":"string"}},
           "against_the_advert":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["requirement","evidence","shown"],
             "properties":{"requirement":{"type":"string"},"evidence":{"type":"string"},"shown":{"type":"string","enum":["Shown","Not shown","Unclear"]}}}}}}
        """;

    private const string QuestionsSchema =
        """
        {"type":"object","additionalProperties":false,"required":["questions"],
         "properties":{"questions":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["question","listen_for"],
           "properties":{"question":{"type":"string"},"listen_for":{"type":"string"}}}}}}
        """;

    private const string DraftSchema =
        """
        {"type":"object","additionalProperties":false,"required":["subject","body"],
         "properties":{"subject":{"type":"string"},"body":{"type":"string"}}}
        """;

    public async Task<AidResult> SummariseAsync(Guid applicationId, Asker asker, CancellationToken cancellationToken = default)
    {
        if (await RefusedAsync(asker, cancellationToken) is { } refused)
        {
            return refused;
        }

        if (await LoadAsync(applicationId, cancellationToken) is not ({ } application, { } candidate, { } posting))
        {
            return new(AnswerStatus.Failed, "There is no such application.", 0);
        }

        var cv = await cvs.ReadAsync(application.CvStoredName, application.CvFileName, cancellationToken);

        if (!cv.IsReadable)
        {
            // Nothing to summarise, and nothing is sent. A summary of the form fields alone would
            // look like a summary of the CV to anybody who did not read this sentence.
            return new(AnswerStatus.Failed, cv.Note, await ledger.RemainingTodayAsync(asker.AccountId, cancellationToken), CvNote: cv.Note);
        }

        var text = new StringBuilder()
            .AppendLine(Advert(posting))
            .AppendLine()
            .AppendLine("What the candidate wrote on the application form:")
            .AppendLine(Profile(candidate));

        if (cv.Text is { } words)
        {
            text.AppendLine().AppendLine("The CV:").AppendLine(words);
        }
        else
        {
            text.AppendLine().AppendLine("The CV is the enclosed document.");
        }

        /*
         * Recorded before it is sent, as a read of the CV. Section 55's access log answers "who
         * opened my CV", and a CV handed to a third party to read is the most consequential opening
         * there is. Recorded even if the provider then fails: the attempt is what the applicant
         * would want to know about.
         */
        await accessLog.ViewedAsync("job_application.cv_sent_to_ai", "JobApplication", application.Id, cancellationToken);

        var (status, problem, reply, took) = await AskAsync(
            SummaryInstructions,
            new Asked(text.ToString(), cv.Enclosure is { } file ? [file] : null),
            SummarySchema,
            cancellationToken);

        var summary = status == AnswerStatus.Answered ? ParseSummary(reply!.Text) : null;

        if (status == AnswerStatus.Answered && summary is null)
        {
            (status, problem) = (AnswerStatus.Failed, Unreadable);
        }

        var remaining = await RecordAsync(asker, AiFeature.CvSummary, application.Id, status, problem, reply, took, cancellationToken);

        return new AidResult(status, problem, remaining, Summary: summary);
    }

    public async Task<AidResult> QuestionsAsync(
        Guid applicationId, string interviewType, Asker asker, CancellationToken cancellationToken = default)
    {
        if (await RefusedAsync(asker, cancellationToken) is { } refused)
        {
            return refused;
        }

        if (await LoadAsync(applicationId, cancellationToken) is not ({ } application, { } candidate, { } posting))
        {
            return new(AnswerStatus.Failed, "There is no such application.", 0);
        }

        var kind = InterviewTypes.FirstOrDefault(one => one == interviewType) ?? "Technical";

        var (status, problem, reply, took) = await AskAsync(
            QuestionsInstructions,
            new Asked($"The interview: {kind}.\n\n{Advert(posting)}\n\nWhat the candidate wrote on the application form:\n{Profile(candidate)}"),
            QuestionsSchema,
            cancellationToken);

        var questions = status == AnswerStatus.Answered ? ParseQuestions(reply!.Text) : null;

        if (status == AnswerStatus.Answered && questions is null)
        {
            (status, problem) = (AnswerStatus.Failed, Unreadable);
        }

        var remaining = await RecordAsync(asker, AiFeature.InterviewQuestions, application.Id, status, problem, reply, took, cancellationToken);

        return new AidResult(status, problem, remaining, Questions: questions);
    }

    public async Task<AidResult> DraftAsync(
        Guid applicationId, LetterKind kind, string? points, Asker asker, CancellationToken cancellationToken = default)
    {
        if (await RefusedAsync(asker, cancellationToken) is { } refused)
        {
            return refused;
        }

        if (await LoadAsync(applicationId, cancellationToken) is not ({ } application, { } candidate, { } posting))
        {
            return new(AnswerStatus.Failed, "There is no such application.", 0);
        }

        var what = kind switch
        {
            LetterKind.Rejection => "a letter telling the candidate they have not been successful",
            LetterKind.Invitation => "a letter inviting the candidate to an interview",
            _ => "a covering letter to go with a written offer of employment; the terms themselves are in the offer, not the letter",
        };

        var (status, problem, reply, took) = await AskAsync(
            DraftInstructions,
            new Asked(
                $"Write {what}.\n\nThe candidate: {candidate.FullName}.\nThe role: {posting.Title}."
                + (string.IsNullOrWhiteSpace(points) ? string.Empty : $"\n\nPoints the recruiter wants made:\n{points.Trim()}")),
            DraftSchema,
            cancellationToken);

        var draft = status == AnswerStatus.Answered ? ParseDraft(reply!.Text) : null;

        if (status == AnswerStatus.Answered && draft is null)
        {
            (status, problem) = (AnswerStatus.Failed, Unreadable);
        }

        var remaining = await RecordAsync(asker, AiFeature.Draft, application.Id, status, problem, reply, took, cancellationToken);

        return new AidResult(status, problem, remaining, Draft: draft);
    }

    private const string Unreadable =
        "The answer came back in a shape this page cannot lay out, so none of it is shown.";

    private async Task<AidResult?> RefusedAsync(Asker asker, CancellationToken cancellationToken)
    {
        if (!model.IsConfigured)
        {
            return new(AnswerStatus.NotConfigured,
                "The recruitment aids need the AI provider, and no API key has been set on this copy of the system.", 0);
        }

        // Both: the aid's own permission, and the one that lets somebody read the candidate at all.
        if (!asker.Holds(Permissions.AiRecruit) || !asker.Holds(Permissions.CandidatesView))
        {
            return new(AnswerStatus.NotPermitted,
                $"You need both {Permissions.AiRecruit} and {Permissions.CandidatesView}.", 0);
        }

        var remaining = await ledger.RemainingTodayAsync(asker.AccountId, cancellationToken);

        return remaining <= 0
            ? new(AnswerStatus.LimitReached,
                $"You have used today's {ledger.DailyLimit} uses. The allowance renews at midnight UTC.", 0)
            : null;
    }

    private async Task<(JobApplication, Candidate, JobPosting)?> LoadAsync(Guid applicationId, CancellationToken cancellationToken)
    {
        var application = await recruitment.FindApplicationAsync(applicationId, cancellationToken);

        if (application is null)
        {
            return null;
        }

        var candidate = await recruitment.FindCandidateAsync(application.CandidateId, cancellationToken);
        var posting = await recruitment.FindPostingAsync(application.PostingId, cancellationToken);

        return candidate is null || posting is null ? null : (application, candidate, posting);
    }

    private static string Advert(JobPosting posting) =>
        $"The advert — {posting.Title}:\n{posting.Summary}\n\n{posting.Description}";

    /// <remarks>
    /// The fields a decision uses and nothing that identifies the person: no name, no contact
    /// details, no links to their profiles, no salary.
    /// </remarks>
    private static string Profile(Candidate candidate) =>
        $"Years of experience: {candidate.YearsOfExperience?.ToString() ?? "not stated"}\n"
        + $"Education: {candidate.Education ?? "not stated"}\n"
        + $"Skills, as they describe them: {candidate.Skills ?? "not stated"}";

    private async Task<(AnswerStatus, string?, ModelReply?, TimeSpan)> AskAsync(
        string instructions, Asked asked, string schema, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();

        try
        {
            var reply = await model.AskAsync(new ModelRequest(instructions, [asked], [], schema), cancellationToken);

            return reply.End switch
            {
                ReplyEnd.Declined => (AnswerStatus.Declined, "The AI provider declined this request.", reply, timer.Elapsed),
                ReplyEnd.Truncated => (AnswerStatus.Incomplete, "The answer ran out of room before it finished. Try again.", reply, timer.Elapsed),
                _ => (AnswerStatus.Answered, null, reply, timer.Elapsed),
            };
        }
        catch (AiUnavailableException exception)
        {
            return (AnswerStatus.Failed, exception.Message, null, timer.Elapsed);
        }
    }

    private async Task<int> RecordAsync(
        Asker asker, AiFeature feature, Guid subject, AnswerStatus status, string? problem,
        ModelReply? reply, TimeSpan took, CancellationToken cancellationToken)
    {
        try
        {
            await ledger.RecordAsync(new AiExchange(
                clock.Now, asker.AccountId, asker.Name, feature, subject, null, string.Empty,
                ProjectReading.Outcome(status), problem, model.Name,
                reply?.InputTokens ?? 0, reply?.OutputTokens ?? 0, took), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "A recruitment aid's use could not be written to the usage log.");
        }

        return await ledger.RemainingTodayAsync(asker.AccountId, cancellationToken);
    }

    internal static CvSummary? ParseSummary(string text) => Parse(text, root => new CvSummary(
        root.GetProperty("summary").GetString() ?? string.Empty,
        Strings(root.GetProperty("skills")),
        root.GetProperty("experience").GetString() ?? string.Empty,
        root.GetProperty("education").GetString() ?? string.Empty,
        Strings(root.GetProperty("missing_information")),
        [.. root.GetProperty("against_the_advert").EnumerateArray().Select(one => new RequirementEvidence(
            one.GetProperty("requirement").GetString() ?? string.Empty,
            one.GetProperty("evidence").GetString() ?? string.Empty,
            one.GetProperty("shown").GetString() ?? string.Empty))]));

    internal static IReadOnlyList<SuggestedQuestion>? ParseQuestions(string text) => Parse(text, root =>
        (IReadOnlyList<SuggestedQuestion>)[.. root.GetProperty("questions").EnumerateArray().Select(one => new SuggestedQuestion(
            one.GetProperty("question").GetString() ?? string.Empty,
            one.GetProperty("listen_for").GetString() ?? string.Empty))]);

    internal static LetterDraft? ParseDraft(string text) => Parse(text, root => new LetterDraft(
        root.GetProperty("subject").GetString() ?? string.Empty,
        root.GetProperty("body").GetString() ?? string.Empty));

    private static IReadOnlyList<string> Strings(JsonElement array) =>
        [.. array.EnumerateArray().Select(one => one.GetString() ?? string.Empty)];

    private static T? Parse<T>(string text, Func<JsonElement, T> read) where T : class
    {
        try
        {
            using var document = JsonDocument.Parse(text);

            return read(document.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}
