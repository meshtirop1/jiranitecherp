using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Notices;

/// <summary>Where an announcement is in its life.</summary>
public enum AnnouncementState
{
    /// <summary>Written and not up yet. Nobody but the person writing it can see it.</summary>
    Draft = 1,

    Posted = 2,

    /// <summary>Taken down before it expired, because it was wrong or overtaken.</summary>
    Withdrawn = 3,
}

/// <summary>
/// One person saying they have read an announcement and understood what it asks of them.
/// </summary>
/// <remarks>
/// An act, not a read receipt. See the note on <see cref="Announcement.NeedsAcknowledgement"/>
/// for why this system records the first and refuses to invent the second.
/// </remarks>
public sealed class Acknowledgement : Entity
{
    private Acknowledgement()
    {
    }

    internal Acknowledgement(Guid employeeId, DateTimeOffset at)
    {
        EmployeeId = employeeId;
        At = at;
    }

    public Guid EmployeeId { get; private init; }

    public DateTimeOffset At { get; private init; }
}

/// <summary>
/// Something the firm is telling everybody.
/// </summary>
/// <remarks>
/// Section 6.
///
/// <b>One row read by many, and that is the whole design.</b> A <see cref="Notice"/> is addressed
/// to one person about one thing that happened to them, so there is a row per person and that is
/// right. An announcement is the opposite shape: one thing, said once, to everybody. Writing it
/// as a notice per employee would mean a firm-wide message costs a row per head, a correction
/// has to find and edit all of them, and somebody hired next week never sees it at all — which
/// is the bug that shape produces rather than a limitation of it.
///
/// <b>Reads are not tracked.</b> Deliberately, and it is the decision in this file most likely
/// to be questioned. A read count on a notice board is a number nobody acts on, and worse than
/// useless: it invites the belief that whoever did not open it does not know, which is neither
/// true nor anything to act on. What can be acted on is an acknowledgement — somebody saying
/// they have read a thing and understood what it asks of them — and that is a real act by a real
/// person, so it is recorded, chased, and asked for only when it is needed.
///
/// <b>Which is why the body freezes.</b> Once anybody has acknowledged an announcement that
/// asked for acknowledgement, its words cannot be changed: those people agreed to what it said
/// at the time, and editing it afterwards would make their acknowledgement a signature on a
/// document somebody else rewrote. Post a new one.
/// </remarks>
public sealed class Announcement : Entity, IAuditable
{
    private readonly List<Acknowledgement> _acknowledgements = [];

    private Announcement()
    {
        Title = string.Empty;
        Body = string.Empty;
    }

    private Announcement(
        string title,
        string body,
        Guid byEmployeeId,
        Guid? departmentId,
        bool needsAcknowledgement,
        DateTimeOffset at)
    {
        Title = Require(title, nameof(title), 200);
        Body = Require(body, nameof(body), 20_000);
        ByEmployeeId = byEmployeeId;
        DepartmentId = departmentId;
        NeedsAcknowledgement = needsAcknowledgement;
        WrittenAt = at;
        State = AnnouncementState.Draft;
    }

    public static Announcement Write(
        string title,
        string body,
        Guid byEmployeeId,
        DateTimeOffset at,
        Guid? departmentId = null,
        bool needsAcknowledgement = false) =>
        new(title, body, byEmployeeId, departmentId, needsAcknowledgement, at);

    public string Title { get; private set; }

    public string Body { get; private set; }

    /// <summary>Who is saying it. A notice board with anonymous posts is a rumour mill.</summary>
    public Guid ByEmployeeId { get; private init; }

    /// <summary>
    /// The department this is for, or null for the whole firm.
    /// </summary>
    /// <remarks>
    /// A department rather than a list of people or a team, because the thing somebody actually
    /// wants to say is "everybody in Delivery" and a list would be out of date by the next hire.
    /// It narrows who sees it on the board; it is not a permission, and this is not the place to
    /// put anything confidential — the board is readable by everybody who works here.
    /// </remarks>
    public Guid? DepartmentId { get; private set; }

    /// <summary>
    /// Whether people are being asked to say they have read it.
    /// </summary>
    /// <remarks>
    /// Off for nearly everything, and that is the point. Most announcements are a thing worth
    /// knowing — the office is shut on Friday — and asking twenty people to click a button about
    /// it teaches them to click the button without reading. Turned on for the few that change
    /// what somebody is expected to do, where the outstanding list is the reason the feature
    /// exists: a new expenses policy, a change to the leave rules, a security requirement.
    /// </remarks>
    public bool NeedsAcknowledgement { get; private set; }

    public AnnouncementState State { get; private set; }

    public DateTimeOffset WrittenAt { get; private init; }

    public DateTimeOffset? PostedAt { get; private set; }

    /// <summary>
    /// When it comes off the board. Null for something that stays up.
    /// </summary>
    /// <remarks>
    /// Because a board with last year's Christmas arrangements on it is a board nobody reads,
    /// and the value of the recent items is destroyed by the old ones sitting next to them.
    /// Coming off the board is not being deleted: the page keeps working and the announcement
    /// stays findable, which matters when somebody asks what the rule was in March.
    /// </remarks>
    public DateOnly? ExpiresOn { get; private set; }

    public string? Outcome { get; private set; }

    public IReadOnlyList<Acknowledgement> Acknowledgements =>
        [.. _acknowledgements.OrderBy(one => one.At).ThenBy(one => one.Id)];

    public bool IsPosted => State == AnnouncementState.Posted;

    public bool HasAnybodyAcknowledged => _acknowledgements.Count > 0;

    public bool AcknowledgedBy(Guid employeeId) =>
        _acknowledgements.Any(one => one.EmployeeId == employeeId);

    /// <summary>Whether it belongs on the board today.</summary>
    public bool IsUpOn(DateOnly today) =>
        IsPosted && (ExpiresOn is null || ExpiresOn >= today);

    public bool HasExpiredOn(DateOnly today) =>
        IsPosted && ExpiresOn is { } expires && expires < today;

    /// <summary>
    /// Change what it says.
    /// </summary>
    /// <remarks>
    /// Allowed on a draft always, and on something already up only while nobody has
    /// acknowledged it — a typo in a posted announcement has to be fixable, or the board fills
    /// with corrections of corrections. Once somebody has acknowledged it the words are theirs
    /// as much as the firm's, and rewriting them would leave a signature on a document that has
    /// changed since.
    /// </remarks>
    public void Say(string title, string body)
    {
        if (State == AnnouncementState.Withdrawn)
        {
            throw new InvalidOperationException(
                "This was taken down. Post a new announcement rather than editing a withdrawn "
                + "one — anybody who read it read what it said at the time.");
        }

        if (HasAnybodyAcknowledged)
        {
            throw new InvalidOperationException(
                $"{_acknowledgements.Count} " + (_acknowledgements.Count == 1 ? "person has" : "people have")
                + " already said they have read this, and they agreed to what it says now. "
                + "Post a new announcement and let it supersede this one.");
        }

        Title = Require(title, nameof(title), 200);
        Body = Require(body, nameof(body), 20_000);
    }

    public void For(Guid? departmentId) => DepartmentId = departmentId;

    /// <summary>
    /// Change whether it asks to be acknowledged, while it is still a draft.
    /// </summary>
    /// <remarks>
    /// Only while it is a draft, and that asymmetry is the point. A draft is nothing the firm has
    /// said yet, so getting the setting wrong should cost a click — and without this it cost the
    /// whole announcement, because nothing here deletes a draft and a withdrawal only applies to
    /// something that went up. That gap was found by writing one on the real screen with the box
    /// unticked and then looking for the way back.
    ///
    /// Once it is up the setting is fixed. Turning it on afterwards would ask people who already
    /// read it to answer a question that was not there when they did; turning it off would throw
    /// away answers people had already given.
    /// </remarks>
    public void Asks(bool needsAcknowledgement)
    {
        if (State != AnnouncementState.Draft)
        {
            throw new InvalidOperationException(
                "This is already up. Whether it asks to be acknowledged is part of what people "
                + "were shown, so it is fixed now — post a new announcement if that has to "
                + "change.");
        }

        NeedsAcknowledgement = needsAcknowledgement;
    }

    /// <summary>How long it stays up.</summary>
    public void Until(DateOnly? expiresOn)
    {
        if (expiresOn is { } expires && PostedAt is { } posted
            && expires < DateOnly.FromDateTime(posted.UtcDateTime))
        {
            throw new ArgumentException(
                "An announcement cannot come off the board before it went up.", nameof(expiresOn));
        }

        ExpiresOn = expiresOn;
    }

    /// <summary>
    /// Put it up.
    /// </summary>
    /// <remarks>
    /// The draft state exists because a firm-wide announcement is the one message that cannot be
    /// unsaid. Somebody writing about a redundancy, a pay change or an incident wants to read it
    /// again before everybody else does, and a system with no draft makes the first draft the
    /// published one.
    /// </remarks>
    public void Post(DateTimeOffset at)
    {
        if (State != AnnouncementState.Draft)
        {
            throw new InvalidOperationException(
                State == AnnouncementState.Posted
                    ? "This is already up."
                    : "This was taken down. Post a new announcement instead.");
        }

        State = AnnouncementState.Posted;
        PostedAt = at;

        Raise(new AnnouncementPosted(
            Id, Title, ByEmployeeId, DepartmentId, NeedsAcknowledgement, at));
    }

    /// <summary>
    /// Take it down, saying why.
    /// </summary>
    /// <remarks>
    /// Not deleted, and the reason is the same one that keeps a withdrawn release: people read
    /// it. An announcement that vanished leaves everybody who acted on it holding an instruction
    /// the system says was never given, and the argument that follows has no record to settle
    /// it.
    /// </remarks>
    public void Withdraw(string why, DateTimeOffset at)
    {
        if (State != AnnouncementState.Posted)
        {
            throw new InvalidOperationException("Only something that is up can be taken down.");
        }

        State = AnnouncementState.Withdrawn;
        Outcome = $"Taken down {at:d MMM yyyy}: {Require(why, nameof(why), 1_000)}";
    }

    /// <summary>
    /// Somebody says they have read it.
    /// </summary>
    /// <remarks>
    /// Idempotent, because the button is on a page people reload and a second row would make the
    /// outstanding count wrong in the direction that matters — it would look as though more
    /// people had answered than have.
    /// </remarks>
    public void Acknowledge(Guid employeeId, DateTimeOffset at)
    {
        if (!NeedsAcknowledgement)
        {
            throw new InvalidOperationException(
                "This announcement does not ask to be acknowledged. Recording one anyway would "
                + "make the acknowledged ones indistinguishable from the ones nobody was asked "
                + "about.");
        }

        if (!IsPosted)
        {
            throw new InvalidOperationException(
                "Nobody can acknowledge something that is not up.");
        }

        if (AcknowledgedBy(employeeId))
        {
            return;
        }

        _acknowledgements.Add(new Acknowledgement(employeeId, at));
    }

    /// <summary>Nothing here is a secret — the board is read by everybody who works here.</summary>
    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private static string Require(string value, string parameter, int longest)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("This cannot be blank.", parameter);
        }

        var trimmed = value.Trim();

        return trimmed.Length > longest
            ? throw new ArgumentException($"This cannot be longer than {longest} characters.", parameter)
            : trimmed;
    }
}

/// <summary>
/// An announcement went up.
/// </summary>
/// <remarks>
/// Carries whether it asks to be acknowledged, because that is what decides whether anything
/// happens beyond the board: an ordinary announcement is read where it is posted, and one that
/// asks for an answer is worth an email.
/// </remarks>
public sealed record AnnouncementPosted(
    Guid AnnouncementId,
    string Title,
    Guid ByEmployeeId,
    Guid? DepartmentId,
    bool NeedsAcknowledgement,
    DateTimeOffset At) : DomainEvent;
