using System.Diagnostics;
using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Ai;
using JiranisokoTech.Application.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JiranisokoTech.Infrastructure.Ai;

/// <summary>How a question went.</summary>
public enum AnswerStatus
{
    Answered = 1,

    /// <summary>It stopped before it finished — out of room, or out of rounds of lookups.</summary>
    Incomplete = 2,

    /// <summary>The provider declined to answer.</summary>
    Declined = 3,

    /// <summary>The provider could not be asked, or failed. <see cref="AssistantAnswer.Problem"/> says which.</summary>
    Failed = 4,

    /// <summary>No key is configured. Nothing was sent.</summary>
    NotConfigured = 5,

    /// <summary>The person does not hold the permission. Nothing was sent.</summary>
    NotPermitted = 6,

    /// <summary>The person has used today's allowance. Nothing was sent.</summary>
    LimitReached = 7,
}

/// <summary>One lookup the model made, as the page lists it.</summary>
public sealed record LookupMade(string Name, string Described, bool Refused);

/// <summary>What the page shows after a question.</summary>
public sealed record AssistantAnswer(
    AnswerStatus Status,
    string? Text,
    IReadOnlyList<LookupMade> Lookups,
    TaskProposal? Proposal,
    string? Problem,
    int Remaining);

/// <summary>
/// Section 36: a question about the firm's records, answered from lookups made as the person asking.
/// </summary>
/// <remarks>
/// A loop, written out rather than hidden in a library: ask, run whatever lookups the model wants,
/// hand the results back, and stop when it answers, is refused, or has used its rounds. Every
/// lookup goes through <see cref="AssistantTools"/>, which is where the permission boundary is;
/// nothing here reads a record itself, so there is no path from a question to the database that
/// does not pass that boundary.
///
/// One question, one answer. There is no conversation carried between questions, so nothing the
/// model saw for one question is still in front of it for the next — which matters when a person's
/// permissions change between the two, and matters to what is held about them afterwards.
/// </remarks>
public sealed class Assistant(
    IAiModel model,
    AssistantTools tools,
    AiLedger ledger,
    IClock clock,
    IOptions<AiOptions> options,
    ILogger<Assistant> logger)
{
    /// <summary>The longest question accepted, in characters.</summary>
    public const int LongestQuestion = 2000;

    /// <summary>
    /// What the model is and how it must behave.
    /// </summary>
    /// <remarks>
    /// Constant, so the provider caches it: nothing that changes between questions — the date, the
    /// person's name — is written here. The date goes in the question instead.
    ///
    /// The two paragraphs that matter most are the last two. A model that is refused a lookup will
    /// otherwise fill the gap from what it can infer, and an inference about an invoice the person
    /// may not see is the leak by another route. And the answer is read by somebody deciding
    /// something, so a conclusion the records do not state must say that it is a conclusion.
    /// </remarks>
    public const string Instructions =
        """
        You answer questions from staff at Jiranisoko Tech Solutions, a software firm in Nairobi, about the firm's own records: projects, work items, pull requests, builds, deployments, clients and invoices.

        You know nothing about the firm except what the lookups return. Use them before answering anything about the firm, and answer only from what they return. If the lookups do not contain the answer, say so. Never invent a record, a figure, a name or a date.

        Each lookup is made as the person asking and returns only what they may see. When a lookup is refused, tell the person plainly that they do not have access to that, name what it was, and stop there. Do not estimate, hint at or reconstruct what a refused lookup would have shown, and do not try to reach the same records another way.

        Keep answers short and plain. Name the records you relied on — project codes, work item references like #42, invoice numbers — so the person can open them. When you say something the records do not state outright, such as why something is late or what is likely to happen next, say that it is your reading rather than a recorded fact.

        You cannot change anything. If asked to create a task, use propose_task; the person decides whether to create it. Never say that anything has been created, sent or changed.
        """;

    public async Task<AssistantAnswer> AskAsync(
        string question, Asker asker, CancellationToken cancellationToken = default)
    {
        if (!model.IsConfigured)
        {
            return new(AnswerStatus.NotConfigured, null, [], null,
                "The assistant is not configured on this copy of the system: no API key has been set.", 0);
        }

        if (!asker.Holds(Permissions.AiAsk))
        {
            return new(AnswerStatus.NotPermitted, null, [], null,
                $"You do not hold {Permissions.AiAsk}.", 0);
        }

        var remaining = await ledger.RemainingTodayAsync(asker.AccountId, cancellationToken);

        if (remaining <= 0)
        {
            return new(AnswerStatus.LimitReached, null, [], null,
                $"You have used today's {ledger.DailyLimit} questions. The allowance renews at midnight UTC.", 0);
        }

        var timer = Stopwatch.StartNew();
        var conversation = new List<ModelTurn>
        {
            new Asked($"Today is {clock.Today:dddd d MMMM yyyy}. The question:\n\n{question.Trim()}"),
        };

        var lookups = new List<LookupMade>();
        TaskProposal? proposal = null;
        var inputTokens = 0;
        var outputTokens = 0;
        string? text = null;
        var status = AnswerStatus.Incomplete;
        string? problem = null;

        try
        {
            for (var round = 0; round < options.Value.MaxRounds; round++)
            {
                var reply = await model.AskAsync(
                    new ModelRequest(Instructions, conversation, tools.Definitions), cancellationToken);

                inputTokens += reply.InputTokens;
                outputTokens += reply.OutputTokens;
                text = string.IsNullOrWhiteSpace(reply.Text) ? text : reply.Text;

                if (reply.End == ReplyEnd.Declined)
                {
                    status = AnswerStatus.Declined;
                    problem = "The AI provider declined to answer that question.";
                    text = null;
                    break;
                }

                if (reply.End != ReplyEnd.WantsTools || reply.Calls.Count == 0)
                {
                    status = reply.End == ReplyEnd.Truncated ? AnswerStatus.Incomplete : AnswerStatus.Answered;
                    problem = reply.End == ReplyEnd.Truncated
                        ? "The answer ran out of room and may stop part way."
                        : null;
                    break;
                }

                var results = new List<ToolResult>();

                foreach (var call in reply.Calls)
                {
                    var outcome = await RunAsync(call, asker, cancellationToken);

                    lookups.Add(new LookupMade(call.Name, AssistantTools.Describe(call.Name), outcome.Refused));
                    proposal = outcome.Proposal ?? proposal;
                    results.Add(new ToolResult(call.Id, outcome.Content, IsError: false));
                }

                conversation.Add(new Replied(reply.Raw));
                conversation.Add(new ToolResults(results));
            }

            if (status == AnswerStatus.Incomplete && problem is null)
            {
                problem = $"It stopped after {options.Value.MaxRounds} rounds of lookups without finishing.";
            }
        }
        catch (AiUnavailableException exception)
        {
            status = AnswerStatus.Failed;
            problem = exception.Message;
            text = null;
        }

        await RecordAsync(asker, question, lookups, status, problem, inputTokens, outputTokens, timer.Elapsed, cancellationToken);

        return new AssistantAnswer(status, text, lookups, proposal, problem, Math.Max(0, remaining - 1));
    }

    /// <summary>
    /// One lookup, with any failure turned into something the model can be told.
    /// </summary>
    /// <remarks>
    /// Caught broadly, because a lookup crosses into the database and a query that throws would
    /// otherwise take the whole question down with nothing recorded. The model is told the lookup
    /// failed — not why, which is a stack trace and none of its business — and can say so.
    /// </remarks>
    private async Task<ToolOutcome> RunAsync(ToolCall call, Asker asker, CancellationToken cancellationToken)
    {
        try
        {
            return await tools.RunAsync(call.Name, call.Input, asker, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "The assistant's lookup {Lookup} failed.", call.Name);

            return new ToolOutcome("That lookup failed on the server. Say it could not be read.", Refused: false);
        }
    }

    /// <summary>
    /// The usage log's entry, written whatever happened once something was sent.
    /// </summary>
    /// <remarks>
    /// Caught broadly as well. The person has their answer by now, and a log that failed to save
    /// should cost them nothing on the screen — it is written to the application log instead, where
    /// whoever looks after the server will see it.
    /// </remarks>
    private async Task RecordAsync(
        Asker asker, string question, List<LookupMade> lookups, AnswerStatus status, string? problem,
        int inputTokens, int outputTokens, TimeSpan took, CancellationToken cancellationToken)
    {
        try
        {
            await ledger.RecordAsync(new AiExchange(
                clock.Now,
                asker.AccountId,
                asker.Name,
                AiFeature.Assistant,
                null,
                question.Trim(),
                string.Join(", ", lookups.Select(lookup => lookup.Refused ? $"{lookup.Name} (refused)" : lookup.Name)),
                status switch
                {
                    AnswerStatus.Answered => AiOutcome.Answered,
                    AnswerStatus.Declined => AiOutcome.Declined,
                    AnswerStatus.Failed => AiOutcome.Failed,
                    _ => AiOutcome.Incomplete,
                },
                problem,
                model.Name,
                inputTokens,
                outputTokens,
                took), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "A use of the assistant could not be written to the usage log.");
        }
    }
}
