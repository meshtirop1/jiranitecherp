using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Support;

/// <summary>
/// Where a ticket is sitting, named for who it is with.
/// </summary>
/// <remarks>
/// Three values, like an incident's, and not the five the brief draws. There is no transition
/// table here, unlike <c>WorkItem</c>: that one earns its place because two of its states are
/// claims about the world outside the firm, whereas every pair of these is legitimately
/// reachable — a resolved ticket the requester answers is open again, and one sitting with the
/// requester is resolved when they never answer. A table whose every cell is allowed is
/// decoration somebody then has to keep true.
///
/// There is no Closed beside Resolved. It exists in other tools to stop the clock and to stop
/// the requester replying, and this design refuses both.
/// </remarks>
public enum TicketStatus
{
    /// <summary>With the firm. The clock is the firm's.</summary>
    Open = 1,

    /// <summary>
    /// Answered, and waiting on the person who asked.
    /// </summary>
    /// <remarks>
    /// A state and not a pause. See <see cref="Ticket.RespondBy"/>: the clock does not stop
    /// here, because a clock that stops when somebody moves a ticket to "with the requester" is
    /// stopped by moving a ticket to "with the requester".
    /// </remarks>
    WithRequester = 2,

    Resolved = 3,
}

/// <summary>
/// How much it matters, said in terms of what the person asking cannot do.
/// </summary>
/// <remarks>
/// The discipline <c>IncidentSeverity</c> already states: a number means whatever the person
/// saying it thinks it means, and it gets argued about at the worst moment. Each of these is a
/// test anybody can apply without asking.
///
/// Three, not four. <c>Priority.Dropping</c> exists on the board because a board needs one card
/// that stops everything, and a complaint from outside must not be able to set it — which is
/// why the hand-off into work tops out below it.
/// </remarks>
public enum TicketPriority
{
    /// <summary>They cannot do the thing at all.</summary>
    Blocking = 1,

    /// <summary>They can work, badly or the long way round.</summary>
    Slowing = 2,

    /// <summary>Nothing is broken. They want to know something.</summary>
    Asking = 3,
}

/// <summary>Which book the person who asked is in.</summary>
/// <remarks>
/// A discriminator and one identifier, the shape <c>Attachment</c> already uses for the thing it
/// is attached to, because the identifier points at two tables and a foreign key can only point
/// at one.
/// </remarks>
public enum Requester
{
    Colleague = 1,
    ClientContact = 2,
}

/// <summary>
/// Who a line in the thread was written for.
/// </summary>
/// <remarks>
/// <b>Two values, and they are the reason this section is not a comment thread on a work
/// item.</b> A work item's comments have one audience — the firm — and nowhere to record that
/// something was said to somebody outside it. Here the difference decides what leaves the
/// building, so it is not a checkbox on one form that somebody forgets to tick. It is which of
/// two methods was called, and the two sit behind different permissions.
/// </remarks>
public enum Audience
{
    /// <summary>Said to colleagues. Never leaves.</summary>
    Inside = 1,

    /// <summary>Said to the person who asked, or said by them.</summary>
    Requester = 2,
}

/// <summary>One line of the thread.</summary>
public sealed class TicketMessage : Entity
{
    private TicketMessage() => Text = string.Empty;

    internal TicketMessage(
        Audience audience, string text, Guid? byEmployeeId, DateTimeOffset at)
    {
        Audience = audience;
        Text = text;
        ByEmployeeId = byEmployeeId;
        At = at;
    }

    public Audience Audience { get; private init; }

    public string Text { get; private init; }

    /// <summary>
    /// Who wrote it, or nothing when it is the requester's own words.
    /// </summary>
    /// <remarks>
    /// Null means the person who asked said this — typed in by whoever took the telephone call,
    /// or their reply pasted in. Not a gap: a firm whose first contact is a phone call has no
    /// account for the caller, and inventing one would put a stranger in the audit trail.
    /// </remarks>
    public Guid? ByEmployeeId { get; private init; }

    public DateTimeOffset At { get; private init; }
}

/// <summary>
/// One request from a named person, with a clock on it and an answer owed.
/// </summary>
/// <remarks>
/// Section 26, and the section is only worth building because of three things neither an
/// incident nor a work item can hold.
///
/// <b>An incident is the firm's statement that something of its own is broken.</b> Its severity
/// is defined by what people outside the room cannot do, and its notes are written by
/// employees. <b>A work item is the firm's decision to do something</b>: one assignee, a
/// transition table, comments written by employees. Neither can hold a requester who is a
/// contact at a client and has no account here and never will. Neither can hold words that
/// leave the building, because neither has anywhere to record that a line was sent to somebody.
/// And neither can hold a promise with a time on it — a work item's due date is a plan the firm
/// may revise and nobody outside knows it, and an incident's timestamps are facts rather than
/// promises.
///
/// So a ticket is a request from a named person, carrying a first-response clock and an
/// obligation to answer that person in words they read. <b>What it refuses to be is a unit of
/// work.</b> The moment the answer is "we will change something" it raises a real work item and
/// keeps its identifier, rather than growing a board of its own. Two records of one thing is the
/// failure this refusal prevents, and it is the commonest one in this class of software.
///
/// <b>The clock is elapsed time and never pauses.</b> Not business hours, because nothing in
/// this database knows when this firm works — the firm's settings hold a trading name, a tax
/// number and an hourly cost, and no hours — so a business-hours clock would be computed
/// against a calendar nobody configured and would fail in the flattering direction, with every
/// target met.
/// </remarks>
public sealed class Ticket : Entity, IAuditable
{
    private readonly List<TicketMessage> _messages = [];

    private Ticket() => Subject = string.Empty;

    private Ticket(
        int number,
        string subject,
        string body,
        TicketPriority priority,
        Requester from,
        Guid requesterId,
        Guid? clientId,
        DateTimeOffset at)
    {
        Number = number;
        Subject = Text(subject, nameof(subject), 300);
        Priority = priority;
        From = from;
        RequesterId = requesterId;
        ClientId = from == Requester.ClientContact ? clientId : null;
        RaisedAt = at;
        Status = TicketStatus.Open;

        var (respond, resolve) = TargetsFor(priority);

        RespondBy = at + respond;
        ResolveBy = at + resolve;

        Waiting(at);

        /*
         * The opening message carries no author, because it is the requester's own words —
         * typed in by whoever took the call. Audience.Requester so that it reads as part of the
         * conversation rather than as a note about it.
         */
        _messages.Add(new TicketMessage(
            Audience.Requester, Text(body, nameof(body), 10_000), null, at));
    }

    public static Ticket Raise(
        int number,
        string subject,
        string body,
        TicketPriority priority,
        Requester from,
        Guid requesterId,
        DateTimeOffset at,
        Guid? clientId = null) =>
        new(number, subject, body, priority, from, requesterId, clientId, at);

    /// <summary>
    /// What the firm promises, per priority.
    /// </summary>
    /// <remarks>
    /// Written here rather than configurable on a screen, and it is the refusal section 53 made
    /// about retention: a table of targets per client or per contract would promise something no
    /// signed paper here says. An agreement carries obligations and deliberately no hours; a
    /// contract carries a value and dates. The first argument about a missed target would then
    /// be settled by reading the ERP instead of the contract.
    /// </remarks>
    public static (TimeSpan Respond, TimeSpan Resolve) TargetsFor(TicketPriority priority) =>
        priority switch
        {
            TicketPriority.Blocking => (TimeSpan.FromHours(4), TimeSpan.FromDays(2)),
            TicketPriority.Slowing => (TimeSpan.FromDays(1), TimeSpan.FromDays(5)),
            TicketPriority.Asking => (TimeSpan.FromDays(2), TimeSpan.FromDays(15)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(priority), priority, "Nothing has decided what this priority promises."),
        };

    public int Number { get; private init; }

    /// <summary>
    /// What a client quotes back on the telephone.
    /// </summary>
    /// <remarks>
    /// Prefixed, and not "#412". A hash and a number is already how this system writes a work
    /// item, and a client reading "#412" down a telephone to somebody looking at the board is
    /// exactly the confusion this section exists to remove.
    /// </remarks>
    public string Reference => $"S{Number}";

    public string Subject { get; private set; }

    public TicketPriority Priority { get; private set; }

    public TicketStatus Status { get; private set; }

    public Requester From { get; private init; }

    /// <summary>
    /// The employee or the client contact who asked.
    /// </summary>
    /// <remarks>
    /// No foreign key, because the column points at one of two tables and a key would have to
    /// choose. Indexed instead.
    /// </remarks>
    public Guid RequesterId { get; private init; }

    /// <summary>
    /// Whose account it is, for a client's ticket.
    /// </summary>
    /// <remarks>
    /// Stored beside the contact rather than derived from them, because a ticket belongs to the
    /// client relationship and not to the person. A contact who leaves is a fact worth keeping,
    /// and the ticket must not depend on them still being there to say whose it was.
    /// </remarks>
    public Guid? ClientId { get; private init; }

    /// <summary>Who is answering it. Null is ordinary — nobody has picked it up yet.</summary>
    public Guid? AssigneeId { get; private set; }

    public DateTimeOffset RaisedAt { get; private init; }

    /// <summary>
    /// When the firm promised to say something, computed at the moment it was raised.
    /// </summary>
    /// <remarks>
    /// Stored rather than computed on read, and that is the decision. A promise is made at a
    /// moment under the rules in force then; recomputing it every time somebody opens the page
    /// means a later change to <see cref="TargetsFor"/> silently re-promises every open ticket,
    /// in whichever direction suits the firm.
    /// </remarks>
    public DateTimeOffset RespondBy { get; private set; }

    public DateTimeOffset ResolveBy { get; private set; }

    /// <summary>
    /// When the firm first said something to the person who asked.
    /// </summary>
    /// <remarks>
    /// Set by the first message written by an employee <em>for the requester</em>, and by
    /// nothing else. An internal note is not a first response — that is the definition this
    /// whole clock rests on, and the one most help desks get wrong: a queue where writing
    /// "looking into it" to your colleagues stops the customer's clock is a queue that reports
    /// answering people it has not answered.
    ///
    /// Set once. A reopened ticket does not get a second first answer, because the firm did
    /// answer, once, and the number measuring how fast must not be improved by the customer
    /// coming back.
    /// </remarks>
    public DateTimeOffset? FirstRespondedAt { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    /// <summary>
    /// Since when somebody has been waiting for the answer currently owed, or nothing.
    /// </summary>
    /// <remarks>
    /// <b>The second clock, and it exists because the first one could only be broken once.</b>
    ///
    /// <see cref="RespondBy"/> is the promise made when the ticket arrived and
    /// <see cref="FirstRespondedAt"/> is set exactly once, on purpose: the number measuring how
    /// fast the firm answers must not be improved by a customer having to come back. But those two
    /// together are what the escalation sweep and the navigation's count were reading, and that
    /// left a hole with a name — <b>the one requester who had to chase was the one the desk's
    /// alarms could not see.</b> Reply, come back a week later, and the ticket is open, unanswered,
    /// and invisible to everything that looks for a broken promise, for ever, because
    /// FirstRespondedAt is not null.
    ///
    /// So this says who is waiting now. It is set when the ticket is raised, cleared the moment
    /// somebody says something to them, and set again when they come back — by their own reply or
    /// by a reopening. Null is the ordinary resting state of a ticket that has been answered and
    /// means the firm owes nothing at this moment.
    /// </remarks>
    public DateTimeOffset? AwaitingSince { get; private set; }

    /// <summary>
    /// When the answer currently owed is due, or nothing when none is owed.
    /// </summary>
    /// <remarks>
    /// Stored for the same reason <see cref="RespondBy"/> is stored, and it is the column every
    /// query about lateness now reads: one comparison against one indexed column, which a
    /// predicate built from a priority and an interval could not be.
    ///
    /// It is the same target the firm promised at the start, measured from when this wait began.
    /// Not a fresh promise of the firm's choosing and not the original deadline either: a client
    /// who comes back on Friday is owed an answer within the window their priority buys, counted
    /// from Friday, and telling them it was already due on Tuesday would be arithmetic about a
    /// question they had not yet asked.
    /// </remarks>
    public DateTimeOffset? AnswerOwedBy { get; private set; }

    /// <summary>
    /// When somebody senior was told that this promise had been missed.
    /// </summary>
    /// <remarks>
    /// A column rather than a computation, and it is the whole of the escalation design.
    ///
    /// The sweep that looks for missed promises runs every half hour, and the condition it looks
    /// for — nobody has answered and the time to do it has gone — stays true until somebody
    /// answers. So without a mark, the same ticket is escalated forty-eight times a day, and a
    /// notice that arrives forty-eight times is one people filter into a folder. <b>An
    /// escalation that trains people to ignore it is worse than no escalation</b>, because the
    /// firm then believes it has one.
    ///
    /// Null is the ordinary state and means only that nobody has been told, which includes every
    /// ticket answered in time. It is not a flag for "late": <see cref="AnswerOverdue"/> answers
    /// that, from a stored promise, without needing this.
    ///
    /// Cleared whenever a fresh wait begins — raised, they came back, reopened, or the priority
    /// corrected — because each of those is a promise that has not been broken yet, and a mark
    /// left over from the last one would mean nobody is told about this one.
    /// </remarks>
    public DateTimeOffset? EscalatedAt { get; private set; }

    /// <summary>The work this turned into, if the answer was to change something.</summary>
    public Guid? WorkItemId { get; private set; }

    public IReadOnlyList<TicketMessage> Messages =>
        [.. _messages.OrderBy(one => one.At).ThenBy(one => one.Id)];

    /// <summary>What the requester is allowed to read, which is what was said to them.</summary>
    public IReadOnlyList<TicketMessage> AsTheySeeIt =>
        [.. Messages.Where(one => one.Audience == Audience.Requester)];

    public bool IsResolved => Status == TicketStatus.Resolved;

    /// <summary>
    /// The firm never answered the first time, and the time to do it has gone.
    /// </summary>
    /// <remarks>
    /// A fact about the firm's record rather than about today's queue, which is why it reads
    /// <see cref="FirstRespondedAt"/> and not <see cref="AnswerOwedBy"/>. It stays true for ever
    /// once it is true, because the firm was late and later answering does not undo that. What is
    /// owed right now is <see cref="AnswerOverdue"/>.
    /// </remarks>
    public bool ResponseOverdue(DateTimeOffset now) =>
        FirstRespondedAt is null && now > RespondBy;

    /// <summary>Somebody is waiting for an answer and the time to give it has gone.</summary>
    /// <remarks>
    /// The one the desk acts on. True for a ticket nobody has ever answered and equally for one
    /// that was answered, came back, and has been sitting since — which is the case the first
    /// clock alone could not see.
    /// </remarks>
    public bool AnswerOverdue(DateTimeOffset now) =>
        AnswerOwedBy is { } owed && now > owed;

    /// <summary>It is not settled, and the time to settle it has gone.</summary>
    public bool ResolutionOverdue(DateTimeOffset now) => !IsResolved && now > ResolveBy;

    /// <summary>A remark to colleagues. Never leaves.</summary>
    public void Note(string text, Guid byEmployeeId, DateTimeOffset at) =>
        _messages.Add(new TicketMessage(
            Audience.Inside, Text(text, nameof(text), 10_000), byEmployeeId, at));

    /// <summary>
    /// Say something to the person who asked.
    /// </summary>
    /// <remarks>
    /// A separate method from <see cref="Note"/> rather than a flag on one, because the
    /// difference decides what leaves the building and a checkbox is a thing somebody forgets
    /// to tick at three in the afternoon. The two also sit behind different permissions, which
    /// a flag could not express.
    /// </remarks>
    public void Reply(
        string text, Guid byEmployeeId, DateTimeOffset at, bool waitingOnThem = false)
    {
        var said = Text(text, nameof(text), 10_000);

        _messages.Add(new TicketMessage(Audience.Requester, said, byEmployeeId, at));

        FirstRespondedAt ??= at;

        /*
         * Nobody is waiting now, and this is the only thing that clears it. Saying something to
         * the person who asked is the whole act the section is arranged around — an internal note
         * does not reach this method, which is why it cannot stop this clock either.
         */
        Answered();

        if (Status != TicketStatus.Resolved)
        {
            Status = waitingOnThem ? TicketStatus.WithRequester : TicketStatus.Open;
        }

        Raise(new TicketAnswered(Id, Number, Reference, Subject, said, byEmployeeId, at));
    }

    /// <summary>
    /// What they said back, typed in by whoever heard it.
    /// </summary>
    /// <remarks>
    /// Puts a ticket that was with them back with the firm, and a resolved one back in the
    /// queue. It does not touch <see cref="FirstRespondedAt"/> — see the note there.
    /// </remarks>
    public void TheySaid(string text, DateTimeOffset at)
    {
        _messages.Add(new TicketMessage(
            Audience.Requester, Text(text, nameof(text), 10_000), null, at));

        if (Status != TicketStatus.Open)
        {
            Status = TicketStatus.Open;
            ResolvedAt = null;
        }

        /*
         * They are waiting again, from now. This is the case the second clock was added for: a
         * ticket answered once and then chased is owed another answer, and before this it was
         * invisible to the sweep and to the count beside the navigation link.
         */
        Waiting(at);
    }

    public void Assign(Guid? employeeId) => AssigneeId = employeeId;

    /// <summary>
    /// Record that somebody senior has been told this promise was missed.
    /// </summary>
    /// <remarks>
    /// Refuses a second time rather than ignoring it, because the caller is a sweep that will be
    /// running again in half an hour and a silently accepted second call is how the notice storm
    /// this column exists to prevent would arrive anyway.
    /// </remarks>
    public void Escalated(DateTimeOffset at)
    {
        if (EscalatedAt is { } already)
        {
            throw new InvalidOperationException(
                $"{Reference} was already escalated at {already:d MMM HH:mm}. Telling somebody "
                + "twice about the same missed promise is what stops them reading the first one.");
        }

        EscalatedAt = at;
    }

    /// <summary>
    /// Change how much it matters, and with it what was promised.
    /// </summary>
    /// <remarks>
    /// Writes a line in the thread naming the move, because the targets are stored and moving
    /// them silently would leave a ticket answered late looking answered on time, with nothing
    /// anywhere to say the goalposts had shifted.
    ///
    /// Recomputed from when it was raised rather than from now, so correcting a priority
    /// somebody typed wrong gives the promise that should have been made, and not a fresh four
    /// hours starting from the correction.
    /// </remarks>
    public void Reprioritise(TicketPriority priority, Guid byEmployeeId, DateTimeOffset at)
    {
        if (priority == Priority)
        {
            return;
        }

        var was = Priority;
        var (respond, resolve) = TargetsFor(priority);

        Priority = priority;
        RespondBy = RaisedAt + respond;
        ResolveBy = RaisedAt + resolve;

        // And what is owed now, from when this wait began rather than from when it arrived.
        if (AwaitingSince is { } since)
        {
            AnswerOwedBy = since + respond;
        }

        /*
         * The escalation is forgotten, because it was about a promise that no longer exists.
         *
         * Both directions matter. Moving a ticket down to a question means the four hours it was
         * escalated for were never owed, and leaving the mark would hide that; moving one up to
         * blocking means a promise that was being kept is now missed, and leaving the mark would
         * mean nobody is ever told.
         *
         * The cost is precise rather than general: correcting a ticket UPWARDS after it has
         * already been escalated escalates it a second time, because the shorter promise is still
         * missed. Downwards does not, because the longer one is not. It is visible either way,
         * because every change writes the line below.
         */
        EscalatedAt = null;

        Note(
            $"Moved from {Said(was)} to {Said(priority)}, so the answer is now owed by "
            + $"{RespondBy.ToLocalTime():d MMM HH:mm}.",
            byEmployeeId,
            at);
    }

    /// <summary>
    /// Settle it, by telling the person who asked.
    /// </summary>
    /// <remarks>
    /// The answer is required and goes into the thread as a message to them. A resolve button
    /// that closes a ticket without saying anything is the one thing in a help desk that makes
    /// a customer certain they were ignored — and a month later it is indistinguishable from
    /// somebody clearing the queue before going on holiday.
    /// </remarks>
    public void Resolve(string answer, Guid byEmployeeId, DateTimeOffset at)
    {
        if (IsResolved)
        {
            throw new InvalidOperationException($"{Reference} is already resolved.");
        }

        Reply(answer, byEmployeeId, at);

        Status = TicketStatus.Resolved;
        ResolvedAt = at;
    }

    /// <summary>Put it back in the queue, and say why.</summary>
    public void Reopen(string why, Guid byEmployeeId, DateTimeOffset at)
    {
        if (!IsResolved)
        {
            throw new InvalidOperationException($"{Reference} is not resolved.");
        }

        /*
         * The reason is checked before anything moves. It used to be trimmed and length-checked
         * inside the Note call on the last line, after Status and ResolvedAt had already been
         * written — so a blank reason, or one over a thousand characters, left the aggregate
         * reopened in memory with no line in the thread saying why. Whether that reached the
         * database depended on whether the caller happened to save afterwards, which is the worst
         * kind of "depends".
         */
        var because = Text(why, nameof(why), 1_000);

        Status = TicketStatus.Open;
        ResolvedAt = null;

        /*
         * Waiting again, from now. A reopened ticket is one somebody chased, so an answer is owed
         * within the window their priority buys — counted from the chase and not from the original
         * arrival, which is a deadline about a question they had not yet asked.
         */
        Waiting(at);

        Note(because, byEmployeeId, at);
    }

    /// <summary>
    /// Record that this became a piece of work.
    /// </summary>
    /// <remarks>
    /// The identifier and nothing else. The ticket does not mirror the work item's state, does
    /// not close when it is deployed, and is not closed by it — the two records mean different
    /// things, and a ticket that resolved itself the moment a branch merged would tell a client
    /// their problem was solved before anybody had said so to them.
    /// </remarks>
    public void BecameWork(Guid workItemId)
    {
        NotAlreadyWork();

        WorkItemId = workItemId;
    }

    /// <summary>
    /// Refuse if this request is already being dealt with as work.
    /// </summary>
    /// <remarks>
    /// Public and separate from <see cref="BecameWork"/> so the caller can ask before it creates
    /// anything. The work item's identifier does not exist until the work service has made one,
    /// so a refusal that only happened afterwards would leave an orphan card on the board with
    /// nothing pointing at it — and the sentence lives here once rather than being written again
    /// at the call site, where the two would eventually differ.
    /// </remarks>
    public void NotAlreadyWork()
    {
        if (WorkItemId is { } already)
        {
            throw new InvalidOperationException(
                $"{Reference} is already being dealt with as a piece of work. Add to that "
                + $"rather than raising a second one for the same request ({already}).");
        }
    }

    /// <summary>
    /// Somebody is waiting from now, so a fresh answer is owed and the escalation is forgotten.
    /// </summary>
    /// <remarks>
    /// The escalation mark is cleared here rather than at each call site, because every one of the
    /// three — raised, they came back, reopened — starts a promise that has not yet been broken,
    /// and a mark left over from the last one would mean nobody is ever told about this one.
    /// </remarks>
    private void Waiting(DateTimeOffset at)
    {
        AwaitingSince = at;
        AnswerOwedBy = at + TargetsFor(Priority).Respond;
        EscalatedAt = null;
    }

    /// <summary>Nothing is owed at this moment.</summary>
    /// <remarks>
    /// The escalation mark is deliberately NOT cleared here. It records that somebody was told
    /// about a promise that was broken, which answering does not undo — and the page shows it so
    /// that whoever picks the ticket up next knows the conversation about it started elsewhere.
    /// </remarks>
    private void Answered()
    {
        AwaitingSince = null;
        AnswerOwedBy = null;
    }

    private static string Said(TicketPriority priority) => priority switch
    {
        TicketPriority.Blocking => "blocking",
        TicketPriority.Slowing => "slowing them down",
        TicketPriority.Asking => "a question",
        _ => throw new ArgumentOutOfRangeException(
            nameof(priority), priority, "Nothing has decided what this priority is called."),
    };

    private static string Text(string value, string parameter, int longest)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("This cannot be blank.", parameter);
        }

        var trimmed = value.Trim();

        return trimmed.Length > longest
            ? throw new ArgumentException(
                $"This cannot be longer than {longest} characters.", parameter)
            : trimmed;
    }
}

/// <summary>
/// The firm said something to the person who asked.
/// </summary>
/// <remarks>
/// Carries the words, because the handler that turns this into a letter has nothing else to put
/// in one — and reading the ticket back out of the database inside a handler would mean the
/// letter could disagree with the thread.
///
/// Deliberately not offered to outside subscribers: the text is a named client's complaint and
/// its answer, and section 40's list exists precisely to stop that leaving by a route nobody
/// decided on.
/// </remarks>
public sealed record TicketAnswered(
    Guid TicketId,
    int Number,
    string Reference,
    string Subject,
    string Said,
    Guid ByEmployeeId,
    DateTimeOffset At) : DomainEvent;
