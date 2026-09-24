using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Incidents;

/// <summary>
/// Whether a review has been agreed or is still somebody's draft.
/// </summary>
/// <remarks>
/// Two, and the line between them is a conversation having happened. A draft is one person's
/// account; an agreed review is the firm's, and only the second one is worth quoting back at
/// somebody in six months.
/// </remarks>
public enum PostmortemStatus
{
    Draft = 1,
    Agreed = 2,
}

/// <summary>
/// Something the firm decided to do about it.
/// </summary>
/// <remarks>
/// <b>It points at a real work item and cannot exist without one.</b> This is the decision the
/// section turns on. Corrective actions kept inside a review are a list nobody looks at again:
/// they are not on the board, they do not appear in anybody's week, and the first time anybody
/// reads them is during the next incident, which is when somebody notices that the same action
/// was agreed last time. Putting them on the board means they compete with everything else for
/// time — which is the point, because that competition is the real decision and hiding it in a
/// document does not make it go away.
///
/// The work item's number is kept here as well as its id. It never changes, and keeping it
/// means a review can be read without joining to the board — which matters because the board
/// row may be closed, moved between projects, or eventually gone.
/// </remarks>
public sealed class CorrectiveAction : Entity
{
    private CorrectiveAction() => Title = string.Empty;

    internal CorrectiveAction(string title, Guid workItemId, int number, DateTimeOffset agreedAt)
    {
        Title = string.IsNullOrWhiteSpace(title)
            ? throw new ArgumentException("An action needs saying.", nameof(title))
            : title.Trim();
        WorkItemId = workItemId;
        Number = number;
        AgreedAt = agreedAt;
    }

    public string Title { get; private init; }

    public Guid WorkItemId { get; private init; }

    /// <summary>The board number, so this reads without a join.</summary>
    public int Number { get; private init; }

    public DateTimeOffset AgreedAt { get; private init; }
}

/// <summary>
/// What the firm learned from an incident.
/// </summary>
/// <remarks>
/// Section 69, and the second half of the brief's fourth critical workflow.
///
/// <b>Four questions, and they are deliberately not "what went wrong".</b> What happened, why
/// it was possible, how it was noticed, and what would have caught it sooner. The third and
/// fourth are the ones that pay: an incident found by a customer telephoning is a different
/// firm from one found by an alert, and the gap between those two is usually the cheapest thing
/// on the list to fix.
///
/// <b>Blameless by construction, not by convention.</b> There is no field for who caused it,
/// and there is nowhere to put one. This is not squeamishness — a review that names a person
/// gets read by that person's manager, and the next person to notice something odd at midnight
/// decides not to mention it. The timeline already records who did what, because that is how
/// the incident was run; the review is about the system that let it happen.
///
/// <b>Agreeing it requires an answer to "and what are we doing about it".</b> Either there are
/// actions on the board, or there is a sentence saying why there is nothing to do. The second
/// is a real answer — some incidents are somebody else's outage, and inventing a task to look
/// diligent wastes a week and teaches people that reviews produce make-work — but it has to be
/// said out loud rather than left as an empty list nobody notices.
/// </remarks>
public sealed class Postmortem : Entity, IAuditable
{
    private readonly List<CorrectiveAction> _actions = [];

    private Postmortem()
    {
        WhatHappened = string.Empty;
        WhyItWasPossible = string.Empty;
        HowItWasNoticed = string.Empty;
        WhatWouldHaveCaughtItSooner = string.Empty;
    }

    private Postmortem(Guid incidentId, Guid startedById, DateTimeOffset at)
    {
        IncidentId = incidentId;
        StartedById = startedById;
        StartedAt = at;
        Status = PostmortemStatus.Draft;
        WhatHappened = string.Empty;
        WhyItWasPossible = string.Empty;
        HowItWasNoticed = string.Empty;
        WhatWouldHaveCaughtItSooner = string.Empty;
    }

    public static Postmortem Begin(Guid incidentId, Guid startedById, DateTimeOffset at) =>
        new(incidentId, startedById, at);

    public Guid IncidentId { get; private init; }

    public PostmortemStatus Status { get; private set; }

    public Guid StartedById { get; private init; }

    public DateTimeOffset StartedAt { get; private init; }

    public Guid? AgreedById { get; private set; }

    public DateTimeOffset? AgreedAt { get; private set; }

    /// <summary>The account, in plain words, for somebody who was not there.</summary>
    public string WhatHappened { get; private set; }

    /// <summary>
    /// Why the system permitted it — not who did it.
    /// </summary>
    /// <remarks>
    /// The root cause section 27 asks for. It lives here rather than on the incident because
    /// writing it during the incident is guessing, and a guess typed into a field marked "root
    /// cause" is believed for years.
    /// </remarks>
    public string WhyItWasPossible { get; private set; }

    /// <summary>An alert, a graph, a colleague, a customer. Usually the cheapest thing to fix.</summary>
    public string HowItWasNoticed { get; private set; }

    public string WhatWouldHaveCaughtItSooner { get; private set; }

    /// <summary>Why nothing is being done, when nothing is being done.</summary>
    public string? NothingToDoBecause { get; private set; }

    /// <remarks>Returns a copy — see the note on Invoice.Lines for why.</remarks>
    public IReadOnlyList<CorrectiveAction> Actions => _actions.ToList();

    public bool IsAgreed => Status == PostmortemStatus.Agreed;

    /// <summary>Are all four questions answered?</summary>
    public bool IsWritten =>
        WhatHappened.Length > 0
        && WhyItWasPossible.Length > 0
        && HowItWasNoticed.Length > 0
        && WhatWouldHaveCaughtItSooner.Length > 0;

    /// <summary>
    /// Write or rewrite the four answers.
    /// </summary>
    /// <remarks>
    /// Freely, while it is a draft. A review is drafted, argued with and rewritten, and a
    /// system that made each sentence final would get one sentence.
    /// </remarks>
    public void Write(
        string whatHappened,
        string whyItWasPossible,
        string howItWasNoticed,
        string whatWouldHaveCaughtItSooner)
    {
        RefuseIfAgreed("rewritten");

        WhatHappened = whatHappened?.Trim() ?? string.Empty;
        WhyItWasPossible = whyItWasPossible?.Trim() ?? string.Empty;
        HowItWasNoticed = howItWasNoticed?.Trim() ?? string.Empty;
        WhatWouldHaveCaughtItSooner = whatWouldHaveCaughtItSooner?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// Record that the firm has agreed to do something, and which board item it is.
    /// </summary>
    /// <remarks>
    /// The work item is made by the application service before this is called, because a domain
    /// object does not get to reach into another aggregate — and because the number has to come
    /// from the board's own sequence rather than be invented here.
    /// </remarks>
    public CorrectiveAction Act(string title, Guid workItemId, int number, DateTimeOffset at)
    {
        RefuseIfAgreed("added to");

        var action = new CorrectiveAction(title, workItemId, number, at);

        _actions.Add(action);

        return action;
    }

    /// <summary>
    /// Drop an action that was added by mistake.
    /// </summary>
    /// <remarks>
    /// The work item it points at is deliberately left alone. Deleting somebody's board item
    /// from here would be this aggregate reaching into another one to destroy something a
    /// person may have already started; the honest result is an action off the review and a
    /// task on the board that somebody can close themselves.
    /// </remarks>
    public void Drop(Guid actionId)
    {
        RefuseIfAgreed("changed");

        _actions.RemoveAll(one => one.Id == actionId);
    }

    /// <summary>
    /// The firm agrees this is what happened and what it is doing about it.
    /// </summary>
    /// <remarks>
    /// Refused unless all four questions are answered and there is either an action or a stated
    /// reason there is none. Both refusals exist because of the same failure: a review that can
    /// be agreed while empty is one that gets agreed while empty, in a meeting that ran over,
    /// and the section of the system that was supposed to make the firm learn something becomes
    /// a folder of blank documents.
    /// </remarks>
    public void Agree(Guid by, DateTimeOffset at, string? nothingToDoBecause = null)
    {
        if (Status == PostmortemStatus.Agreed)
        {
            return;
        }

        if (!IsWritten)
        {
            throw new InvalidOperationException(
                "All four questions need an answer before this can be agreed. The one people "
                + "leave out is how it was noticed, and it is usually the one worth the most.");
        }

        if (_actions.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(nothingToDoBecause))
            {
                throw new InvalidOperationException(
                    "There are no actions, so say why there is nothing to do. That is a real "
                    + "answer — an outage at somebody else's provider may need nothing from us "
                    + "— but it has to be said rather than left as an empty list.");
            }

            NothingToDoBecause = nothingToDoBecause.Trim();
        }

        Status = PostmortemStatus.Agreed;
        AgreedById = by;
        AgreedAt = at;

        Raise(new PostmortemAgreed(Id, IncidentId, _actions.Count, at));
    }

    /// <summary>
    /// Open it again, with the reason kept.
    /// </summary>
    /// <remarks>
    /// Because a review is sometimes agreed and then something is learned — the cause was not
    /// what everybody thought. Reopening keeps the record that it was once agreed, in the audit
    /// trail, which is the difference between correcting a document and quietly replacing one.
    /// </remarks>
    public void Reopen()
    {
        if (Status == PostmortemStatus.Draft)
        {
            return;
        }

        Status = PostmortemStatus.Draft;
        AgreedById = null;
        AgreedAt = null;
    }

    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private void RefuseIfAgreed(string verb)
    {
        if (Status == PostmortemStatus.Agreed)
        {
            throw new InvalidOperationException(
                $"This review has been agreed, so it cannot be {verb}. Reopen it first, which "
                + "leaves a record that it was agreed and then changed.");
        }
    }
}

/// <summary>
/// A review is agreed.
/// </summary>
/// <remarks>
/// Carries the number of actions rather than the actions, because whatever listens to this
/// months from now should be counting what the firm committed to and reading the board for
/// whether it happened — not holding a copy of a list that has since moved on.
/// </remarks>
public sealed record PostmortemAgreed(
    Guid PostmortemId,
    Guid IncidentId,
    int Actions,
    DateTimeOffset At) : DomainEvent;
