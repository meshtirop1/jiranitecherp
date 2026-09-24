using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Notices;

/// <summary>
/// What a notice is about.
/// </summary>
/// <remarks>
/// A short, closed list rather than a free-text category, because the kind is what a person
/// turns email off for — and a preference screen built on strings somebody typed is one where
/// muting "work.assigned" quietly fails to mute "Work assigned".
///
/// Every one of these corresponds to a domain event that already existed. Nothing here invents
/// a reason to tell somebody something; it addresses what the system was already saying.
/// </remarks>
public enum NoticeKind
{
    /// <summary>Somebody gave you a piece of work.</summary>
    WorkGiven = 1,

    /// <summary>A piece of work left your list.</summary>
    /// <remarks>
    /// Its own kind rather than the same one, because they want opposite reactions and because
    /// work quietly leaving somebody's list is the most common way it gets dropped.
    /// </remarks>
    WorkTaken = 2,

    /// <summary>Something you asked for was approved or refused.</summary>
    RequestSettled = 3,

    /// <summary>Something you own is broken.</summary>
    IncidentRaised = 4,
}

/// <summary>
/// One thing that happened, addressed to one person.
/// </summary>
/// <remarks>
/// Section 32.
///
/// <b>This is a record of things that happened, not a list of things to do.</b> The home page
/// has answered "what is waiting on you" since section 35, decided from what the reader may do
/// rather than from anything stored — and building a second list of tasks here would mean two
/// answers to one question, disagreeing whenever a rule changed. A notice says a thing occurred
/// and that somebody thought you would want to know; the work list says what is outstanding.
/// One is history and one is a state.
///
/// <b>Written by event handlers, through the outbox.</b> Nothing calls "tell somebody" from a
/// page. That means a notice cannot exist for something that then failed to save — an email
/// about a hire that never happened cannot be unsent, and neither can a notice, which is worse
/// because it stays on the screen.
///
/// <b>Read is a fact about this person, and there is no delete.</b> Anything that can be
/// deleted from a history is a history nobody can rely on; anything that must be dealt with is
/// on the work list, where dealing with it is a real action rather than a tidy-up.
/// </remarks>
public sealed class Notice : Entity
{
    private Notice()
    {
        Subject = string.Empty;
    }

    private Notice(
        Guid forEmployeeId, NoticeKind kind, string subject, string? link, DateTimeOffset at)
    {
        ForEmployeeId = forEmployeeId;
        Kind = kind;
        Subject = string.IsNullOrWhiteSpace(subject)
            ? throw new ArgumentException("A notice has to say something.", nameof(subject))
            : subject.Trim();
        Link = string.IsNullOrWhiteSpace(link) ? null : link.Trim();
        At = at;
    }

    public static Notice For(
        Guid forEmployeeId,
        NoticeKind kind,
        string subject,
        DateTimeOffset at,
        string? link = null) =>
        new(forEmployeeId, kind, subject, link, at);

    public Guid ForEmployeeId { get; private init; }

    public NoticeKind Kind { get; private init; }

    /// <summary>One line. It is read in a list of twenty.</summary>
    public string Subject { get; private init; }

    /// <summary>Where to go about it.</summary>
    /// <remarks>
    /// Optional, because some things are worth knowing and have nowhere to go — and a notice
    /// whose link goes to a page that no longer exists is worse than one with no link at all.
    /// </remarks>
    public string? Link { get; private init; }

    public DateTimeOffset At { get; private init; }

    public DateTimeOffset? ReadAt { get; private set; }

    public bool IsUnread => ReadAt is null;

    public void Read(DateTimeOffset at) => ReadAt ??= at;

    /// <summary>
    /// Mark it unread again.
    /// </summary>
    /// <remarks>
    /// Because people open something on a telephone, decide it needs ten minutes they do not
    /// have, and want it back. Without this they leave the whole list unread instead, and the
    /// count stops meaning anything.
    /// </remarks>
    public void NotRead() => ReadAt = null;
}

/// <summary>
/// What one person wants emailed as well as recorded.
/// </summary>
/// <remarks>
/// Section 59, and one row per person per kind — absent means the kind's default.
///
/// <b>Only email can be turned off.</b> The notice itself is always recorded, and that is the
/// decision this class turns on. A record with holes in it is worse than no record: somebody
/// who muted a kind in March cannot tell in September whether a thing did not happen or merely
/// was not written down. What a person is choosing here is what interrupts them, not what the
/// system remembers — and phrasing it that way is what stops "I turned notifications off and
/// missed it" being a thing that can happen.
/// </remarks>
public sealed class NoticeRule : Entity
{
    private NoticeRule()
    {
    }

    private NoticeRule(Guid employeeId, NoticeKind kind, bool byEmail)
    {
        EmployeeId = employeeId;
        Kind = kind;
        ByEmail = byEmail;
    }

    public static NoticeRule For(Guid employeeId, NoticeKind kind, bool byEmail) =>
        new(employeeId, kind, byEmail);

    public Guid EmployeeId { get; private init; }

    public NoticeKind Kind { get; private init; }

    public bool ByEmail { get; private set; }

    public void Email(bool byEmail) => ByEmail = byEmail;

    /// <summary>
    /// Whether a kind is emailed when nobody has said otherwise.
    /// </summary>
    /// <remarks>
    /// Work arriving and requests being settled are emailed by default, because both are things
    /// a person is waiting on and neither happens often enough to be noise. Work leaving a list
    /// is not: it is worth recording and it is not worth an interruption, and a firm where
    /// reassigning three items sends three emails is a firm where people filter the address.
    ///
    /// An incident is emailed, and that is the one anybody would argue with — a critical
    /// incident at three in the morning reaches an inbox nobody is reading. It is emailed
    /// anyway because the alternative is a record with nothing behind it, and because this
    /// system says plainly on the incident screen that it pages nobody: the email is a note for
    /// the morning, not an alert, and it is better to have sent it.
    /// </remarks>
    public static bool EmailedByDefault(NoticeKind kind) => kind switch
    {
        NoticeKind.WorkTaken => false,
        _ => true,
    };
}
