using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Automation;

/// <summary>Where one firing of a rule has got to.</summary>
public enum AutomationRunStatus
{
    /// <summary>Matched, and waiting out the rule's delay.</summary>
    Waiting = 1,

    /// <summary>On the outbox, to be carried out at the next pass.</summary>
    Queued = 2,

    /// <summary>Every action was carried out.</summary>
    Done = 3,

    /// <summary>
    /// Finished, and at least one action was refused — a project that had closed, a person who
    /// has left. Retrying would be refused the same way, so it is not retried.
    /// </summary>
    DoneWithRefusals = 4,

    /// <summary>An action failed in a way that might pass next time, and the outbox will retry.</summary>
    Retrying = 5,

    /// <summary>Failed as many times as the outbox tries anything, and set aside.</summary>
    GaveUp = 6,

    /// <summary>
    /// Not run, on purpose: it would have been a loop, too deep a chain, or too many in an hour.
    /// </summary>
    Suppressed = 7,

    /// <summary>The rule was switched off, or changed its mind, before a delayed run came due.</summary>
    Cancelled = 8,
}

/// <summary>
/// One time a rule fired: the event that fired it, and what came of each action.
/// </summary>
/// <remarks>
/// The history section 31 needs to be trusted. A rule that acts with nobody watching has to
/// leave a record a person can read afterwards — what it saw, what it did, what it was refused
/// and why — or the first time it does something odd, the only explanation available is "the
/// system did it".
///
/// <b>One run per rule per event, enforced by the database.</b> The outbox delivers at least
/// once, so the handler that matches rules can see one event twice; the unique index on the
/// rule and the outbox message is what makes the second sighting a no-op rather than a second
/// batch of work.
///
/// <b>The event is kept</b>, as the outbox keeps it, so that a run delayed by a day acts on the
/// event that fired it rather than on whatever the records say a day later.
///
/// Not audited. It is itself a record of what happened, written by the system; auditing it
/// would record, for every run, that the system wrote down that it ran.
/// </remarks>
public sealed class AutomationRun : Entity
{
    /// <summary>
    /// How deep a chain of rules may go.
    /// </summary>
    /// <remarks>
    /// A rule's action raises an event, which fires a second rule, whose action fires a third.
    /// Three is enough for anything the brief describes — a client taken on starts a workspace
    /// project, the project's own rule sets up its tasks — and short enough that two rules
    /// feeding each other stop on the third lap rather than filling the board.
    /// </remarks>
    public const int DeepestChain = 3;

    /// <summary>
    /// How many times one rule may fire in an hour.
    /// </summary>
    /// <remarks>
    /// The fan-out guard nothing else provides. Importing four hundred clients from a file is a
    /// legitimate thing to do, and a rule on "client taken on" would otherwise answer it with
    /// four hundred emails to the finance manager inside a minute. Past this, the runs are
    /// recorded as suppressed, so the history says exactly how many were held back.
    /// </remarks>
    public const int MostPerHour = 60;

    private readonly List<AutomationRunStep> _steps = [];

    private AutomationRun()
    {
        Trigger = string.Empty;
        Payload = string.Empty;
        Summary = string.Empty;
        RuleName = string.Empty;
    }

    private AutomationRun(
        AutomationRule rule,
        Guid sourceMessageId,
        string payload,
        string summary,
        IReadOnlyList<Guid> chain,
        DateTimeOffset at)
    {
        RuleId = rule.Id;
        RuleName = rule.Name;
        Trigger = rule.Trigger;
        SourceMessageId = sourceMessageId;
        Payload = payload;
        Summary = Shorten(summary, 500);
        Chain = Stamp(chain);
        MatchedAt = at;
    }

    /// <summary>
    /// A rule's conditions held for this event. Queued at once, or waiting out its delay.
    /// </summary>
    public static AutomationRun Matched(
        AutomationRule rule,
        Guid sourceMessageId,
        string payload,
        string summary,
        IReadOnlyList<Guid> chain,
        DateTimeOffset at)
    {
        var run = new AutomationRun(rule, sourceMessageId, payload, summary, chain, at);

        if (rule.DelayMinutes > 0)
        {
            run.Status = AutomationRunStatus.Waiting;
            run.DueAt = at.AddMinutes(rule.DelayMinutes);
        }
        else
        {
            run.Queue(at);
        }

        return run;
    }

    /// <summary>
    /// A rule matched and was deliberately not run. Recorded so the history says so.
    /// </summary>
    public static AutomationRun Held(
        AutomationRule rule,
        Guid sourceMessageId,
        string summary,
        IReadOnlyList<Guid> chain,
        string why,
        DateTimeOffset at)
    {
        var run = new AutomationRun(rule, sourceMessageId, "{}", summary, chain, at)
        {
            Status = AutomationRunStatus.Suppressed,
            FinishedAt = at,
        };

        run.Error = Shorten(why, 2000);

        return run;
    }

    public Guid RuleId { get; private init; }

    /// <summary>The rule's name when it fired, so the history reads correctly after a rename.</summary>
    public string RuleName { get; private init; }

    public string Trigger { get; private init; }

    /// <summary>The outbox message that carried the event. With the rule, unique.</summary>
    public Guid SourceMessageId { get; private init; }

    /// <summary>The event, as the outbox stored it.</summary>
    public string Payload { get; private init; }

    /// <summary>One line saying what the event was, for the history.</summary>
    public string Summary { get; private init; }

    /// <summary>
    /// The rules whose actions led to this event, oldest first, as comma-separated ids.
    /// </summary>
    /// <remarks>
    /// Empty for an event a person or a host caused. This is how a rule recognises its own
    /// work coming back to it, which is the loop section 31's engine has to refuse.
    /// </remarks>
    public string? Chain { get; private init; }

    public AutomationRunStatus Status { get; private set; }

    public DateTimeOffset MatchedAt { get; private init; }

    /// <summary>When a waiting run comes due.</summary>
    public DateTimeOffset? DueAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>How many times carrying it out has failed. The outbox counts the same attempts.</summary>
    public int Attempts { get; private set; }

    /// <summary>Why it failed, was held back or was cancelled.</summary>
    public string? Error { get; private set; }

    public IReadOnlyList<AutomationRunStep> Steps => [.. _steps.OrderBy(one => one.Order)];

    public IReadOnlyList<Guid> ChainIds => Parse(Chain);

    public bool IsSettled => Status is AutomationRunStatus.Done
        or AutomationRunStatus.DoneWithRefusals
        or AutomationRunStatus.Suppressed
        or AutomationRunStatus.Cancelled;

    /// <summary>A waiting run has come due, or a matched one is to go at once.</summary>
    public void Queue(DateTimeOffset at)
    {
        if (Status is not ((AutomationRunStatus)0 or AutomationRunStatus.Waiting))
        {
            return;
        }

        Status = AutomationRunStatus.Queued;
        DueAt = at;

        Raise(new AutomationRunDue(Id, RuleId));
    }

    public void Cancel(string why, DateTimeOffset at)
    {
        if (IsSettled)
        {
            return;
        }

        Status = AutomationRunStatus.Cancelled;
        Error = Shorten(why, 2000);
        FinishedAt = at;
    }

    /// <summary>The step recorded for an action, if it has been tried before.</summary>
    public AutomationRunStep? StepFor(Guid actionId) =>
        _steps.FirstOrDefault(one => one.ActionId == actionId);

    /// <summary>Start, or continue, the record of one action.</summary>
    public AutomationRunStep Begin(AutomationAction action, string describing)
    {
        if (StepFor(action.Id) is { } already)
        {
            return already;
        }

        var step = new AutomationRunStep(action.Id, action.Order, describing);

        _steps.Add(step);

        return step;
    }

    public void Failed(string error, int giveUpAfter)
    {
        Attempts++;
        Error = Shorten(error, 2000);
        Status = Attempts >= giveUpAfter ? AutomationRunStatus.GaveUp : AutomationRunStatus.Retrying;
    }

    public void Finish(DateTimeOffset at)
    {
        Status = _steps.Any(one => one.Refused)
            ? AutomationRunStatus.DoneWithRefusals
            : AutomationRunStatus.Done;
        FinishedAt = at;
        Error = null;
    }

    /// <summary>Written as the outbox writes the causation column: ids, comma-separated.</summary>
    public static string? Stamp(IReadOnlyList<Guid> chain) =>
        chain.Count == 0 ? null : string.Join(',', chain.Select(one => one.ToString("D")));

    public static IReadOnlyList<Guid> Parse(string? chain) =>
        string.IsNullOrWhiteSpace(chain)
            ? []
            : [.. chain.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(one => Guid.TryParse(one, out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty)];

    private static string Shorten(string text, int longest) =>
        text.Length > longest ? text[..longest] : text;
}

/// <summary>What happened to one action in one run.</summary>
public sealed class AutomationRunStep : Entity
{
    private AutomationRunStep()
    {
        Describing = string.Empty;
    }

    internal AutomationRunStep(Guid actionId, int order, string describing)
    {
        ActionId = actionId;
        Order = order;
        Describing = describing.Length > 300 ? describing[..300] : describing;
    }

    /// <summary>The action this is the record of. Not a foreign key: actions can be removed later.</summary>
    public Guid ActionId { get; private init; }

    public int Order { get; private init; }

    /// <summary>What the action was, in words, as it stood when the run began.</summary>
    public string Describing { get; private init; }

    /// <summary>What it did — "Raised JT-42", "Told 3 people".</summary>
    public string? Outcome { get; private set; }

    /// <summary>
    /// What it made, when it made one thing — the work item's id.
    /// </summary>
    /// <remarks>
    /// Recorded the moment the thing exists, before the rest of the action is done. If the run
    /// then fails and is retried, the step finds its work item already raised and does not
    /// raise a second one — which is the whole of what makes raising work safe to retry.
    /// </remarks>
    public Guid? MadeId { get; private set; }

    public DateTimeOffset? DoneAt { get; private set; }

    public bool Refused { get; private set; }

    public bool IsDone => DoneAt is not null;

    public void Made(Guid id) => MadeId = id;

    public void Did(string outcome, DateTimeOffset at)
    {
        Outcome = outcome.Length > 1000 ? outcome[..1000] : outcome;
        DoneAt = at;
        Refused = false;
    }

    /// <summary>
    /// The service said no, in words. Final: the same request would be refused again.
    /// </summary>
    public void WasRefused(string why, DateTimeOffset at)
    {
        Outcome = why.Length > 1000 ? why[..1000] : why;
        DoneAt = at;
        Refused = true;
    }
}

/// <summary>A run is ready to be carried out.</summary>
/// <remarks>
/// The seam that puts actions through the outbox. Carrying out a rule is a handler of this
/// event rather than a loop inside the matcher, so that each run is retried on its own, with
/// the outbox's backoff, and set aside after the outbox's number of attempts — the same
/// semantics as every other reaction in the application, rather than a second retry policy
/// arguing with the first.
/// </remarks>
public sealed record AutomationRunDue(Guid RunId, Guid RuleId) : DomainEvent;
