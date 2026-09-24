using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Incidents;

/// <summary>
/// How bad it is, said in terms of who cannot do what.
/// </summary>
/// <remarks>
/// Three, and named rather than numbered. "Sev 2" means whatever the person saying it thinks it
/// means, and the number is argued about during the incident — which is the worst possible time
/// to be having a definitional conversation. Each of these is defined by what a person outside
/// the room can no longer do, because that is the only definition everybody can apply at three
/// in the morning without asking.
///
/// The severity is expected to change as an incident is understood, and changing it writes a
/// line in the timeline rather than quietly overwriting a column. "We thought it was minor for
/// the first forty minutes" is the single most useful sentence in most reviews, and a system
/// that only stores the final value deletes it.
/// </remarks>
public enum IncidentSeverity
{
    /// <summary>Nobody can do the thing. The service is down, or wrong in a way people act on.</summary>
    Critical = 1,

    /// <summary>Some people cannot do some things, or everybody is slowed badly.</summary>
    Major = 2,

    /// <summary>It is wrong, it is noticed, and people can still work.</summary>
    Minor = 3,
}

/// <summary>
/// Where an incident has got to.
/// </summary>
/// <remarks>
/// <b><see cref="Mitigated"/> is the state this enum exists for.</b> Most tools have open and
/// closed, so the moment the bleeding stops is recorded as the moment the incident ended — and
/// every duration anybody computes afterwards is then answering a question nobody asked. The
/// two are genuinely different: a feature flag turned off at 02:14 stopped the harm, and the
/// bug that made it possible was still there at nine the next morning. People need the first
/// number and the firm needs the second.
/// </remarks>
public enum IncidentStatus
{
    /// <summary>It is happening.</summary>
    Open = 1,

    /// <summary>The harm has stopped. The cause has not been dealt with.</summary>
    Mitigated = 2,

    /// <summary>The cause is gone.</summary>
    Resolved = 3,
}

/// <summary>
/// What a line in the timeline is.
/// </summary>
/// <remarks>
/// Three kinds, because a review reads the timeline asking three different questions: what did
/// we see, what did we do, and what did we decide. Untyped notes make all three the same colour
/// and the answer to "when did we know" has to be reconstructed by reading every line.
/// </remarks>
public enum NoteKind
{
    /// <summary>Something was seen: an alert, a graph, a customer saying so.</summary>
    Observation = 1,

    /// <summary>Somebody did something: restarted, rolled back, turned a flag off.</summary>
    Action = 2,

    /// <summary>Somebody decided something, including deciding to wait.</summary>
    Decision = 3,
}

/// <summary>
/// One line of the timeline.
/// </summary>
/// <remarks>
/// Never edited and never deleted, which is the whole value of it. A timeline that can be
/// tidied up afterwards is a story rather than a record, and the difference matters precisely
/// when somebody wants to know why an obvious thing was not obvious at the time.
///
/// <see cref="At"/> is separate from when the line was typed, because during an incident people
/// write things down afterwards — "14:02, the queue started backing up" entered at 14:40 is
/// normal and useful. <see cref="WrittenAt"/> keeps the other one, so a line added a week later
/// is visible as such.
/// </remarks>
public sealed class IncidentNote : Entity
{
    private IncidentNote() => Text = string.Empty;

    internal IncidentNote(
        DateTimeOffset at, DateTimeOffset writtenAt, Guid byId, string text, NoteKind kind)
    {
        At = at;
        WrittenAt = writtenAt;
        ById = byId;
        Text = string.IsNullOrWhiteSpace(text)
            ? throw new ArgumentException("A timeline line cannot be blank.", nameof(text))
            : text.Trim();
        Kind = kind;
    }

    /// <summary>When the thing happened.</summary>
    public DateTimeOffset At { get; private init; }

    /// <summary>When it was written down.</summary>
    public DateTimeOffset WrittenAt { get; private init; }

    public Guid ById { get; private init; }

    public string Text { get; private init; }

    public NoteKind Kind { get; private init; }

    /// <summary>Was this written well after the fact?</summary>
    /// <remarks>
    /// Ten minutes, because anything inside that is somebody typing while things happen. Shown
    /// on the page so that a line reconstructed from memory a day later is not read as
    /// contemporaneous — which is exactly the mistake a review is prone to.
    /// </remarks>
    public bool WrittenLater => WrittenAt - At > TimeSpan.FromMinutes(10);
}

/// <summary>
/// Something is wrong, and people are dealing with it.
/// </summary>
/// <remarks>
/// Section 27, and the first half of the brief's fourth critical workflow — the only one of the
/// four that had nothing at all. The other half is <see cref="Postmortem"/>.
///
/// <b>Three timestamps, and they are three different facts.</b> When it started, when somebody
/// noticed, when the harm stopped. Most of what anybody wants to know afterwards is a
/// subtraction between two of them: how long we were broken before we knew, how long we took to
/// stop it, how long the cause survived. A single "created" column answers none of those, and
/// the one people most want — time to detect — is the one that needs a field nobody would think
/// to add, because it is a judgement made later rather than a moment somebody clicked.
///
/// <b>The timeline is the artefact.</b> Everything else on here is a summary of it. Lines are
/// appended and never edited, severity changes write their own line, and mitigating and
/// resolving do too — so the record of what was known when is made by using the thing rather
/// than by somebody reconstructing it afterwards, which is the only way it ever gets made.
///
/// <b>It does not page anybody, and nothing here is a status page.</b> Raising an incident
/// writes it down; telling people is done by whatever the firm uses to tell people. A button
/// that looked like it alerted the on-call engineer and did not would be the most dangerous
/// control in this application.
/// </remarks>
public sealed class Incident : Entity, IAuditable
{
    private readonly List<IncidentNote> _notes = [];

    private Incident()
    {
        Title = string.Empty;
    }

    private Incident(
        int number,
        string title,
        IncidentSeverity severity,
        DateTimeOffset startedAt,
        DateTimeOffset reportedAt,
        Guid reportedById,
        string? affects)
    {
        Number = number;
        Title = Required(title, nameof(title));
        Severity = severity;
        StartedAt = startedAt;
        ReportedAt = reportedAt;
        ReportedById = reportedById;
        Affects = Trimmed(affects);
        Status = IncidentStatus.Open;

        Raise(new IncidentRaised(Id, number, Title, severity, startedAt, reportedAt));
    }

    public static Incident Report(
        int number,
        string title,
        IncidentSeverity severity,
        DateTimeOffset startedAt,
        DateTimeOffset reportedAt,
        Guid reportedById,
        string? affects = null) =>
        new(number, title, severity, startedAt, reportedAt, reportedById, affects);

    /// <summary>A short number somebody can say out loud.</summary>
    /// <remarks>
    /// The same reasoning as a work item's: people refer to an incident while doing something
    /// else — in a message, on a call, in a commit — and nobody says a GUID.
    /// </remarks>
    public int Number { get; private init; }

    public string Title { get; private set; }

    public IncidentSeverity Severity { get; private set; }

    public IncidentStatus Status { get; private set; }

    /// <summary>
    /// When it began being wrong, as best anybody can tell.
    /// </summary>
    /// <remarks>
    /// A judgement rather than a fact, and correctable, which is why it has its own method. It
    /// is nearly always wrong at the moment an incident is raised and nearly always known an
    /// hour later, once somebody has looked at a graph. Time to detect is measured from here,
    /// so leaving it at "whenever we noticed" would report that this firm detects everything
    /// instantly.
    /// </remarks>
    public DateTimeOffset StartedAt { get; private set; }

    /// <summary>When somebody raised it. Not a judgement, and never changed.</summary>
    public DateTimeOffset ReportedAt { get; private init; }

    public Guid ReportedById { get; private init; }

    /// <summary>When the harm stopped.</summary>
    public DateTimeOffset? MitigatedAt { get; private set; }

    /// <summary>When the cause was gone.</summary>
    public DateTimeOffset? ResolvedAt { get; private set; }

    /// <summary>
    /// Who is running it.
    /// </summary>
    /// <remarks>
    /// One person, and nullable because a small firm's first ten minutes have nobody in charge
    /// and pretending otherwise would put a name against a role nobody had taken. What it is for
    /// is the question "who do I ask", which during an incident is worth more than any field
    /// here.
    /// </remarks>
    public Guid? LeadId { get; private set; }

    /// <summary>
    /// Which service, from section 14's catalogue.
    /// </summary>
    /// <remarks>
    /// This was free text with a note saying a catalogue would change it, and section 14 has
    /// changed it. What it buys is the step from "the despatch board is down" to who owns it,
    /// what it runs on and which repository it is built from — which is the one link section
    /// 91's chain could not make from an incident.
    ///
    /// Nullable, because the first two minutes of an incident are not the moment to make
    /// somebody classify it, and because the thing that is wrong is sometimes not in the
    /// catalogue at all.
    /// </remarks>
    public Guid? ServiceId { get; private set; }

    /// <summary>What is affected, in the firm's own words.</summary>
    /// <remarks>
    /// Kept beside the catalogue rather than replaced by it, because the useful sentence is
    /// usually narrower than the service: "the despatch board" names what broke, and "only the
    /// Mombasa depot, and only on the mobile app" is what somebody actually needs to read.
    /// </remarks>
    public string? Affects { get; private set; }

    /// <summary>
    /// What broke, in one line, written at resolution.
    /// </summary>
    /// <remarks>
    /// Deliberately not the root cause, which lives on the postmortem. This is the sentence
    /// somebody can write while closing the incident — "the cache was never invalidated after a
    /// price change" — and the difference between it and a root cause is that this says what
    /// broke and the review says why it was possible. Asking for the second one here would get
    /// the first one typed into it, and the review would then be skipped because the field
    /// looked filled in.
    /// </remarks>
    public string? Cause { get; private set; }

    /// <remarks>Returns a copy — see the note on Invoice.Lines for why.</remarks>
    public IReadOnlyList<IncidentNote> Notes => _notes.ToList();

    public bool IsOpen => Status == IncidentStatus.Open;

    public bool IsOver => Status == IncidentStatus.Resolved;

    /// <summary>How long nobody knew.</summary>
    public TimeSpan ToDetect => ReportedAt - StartedAt;

    /// <summary>How long the harm lasted, from when it started.</summary>
    public TimeSpan? ToMitigate => MitigatedAt is { } at ? at - StartedAt : null;

    public TimeSpan? ToResolve => ResolvedAt is { } at ? at - StartedAt : null;

    /// <summary>
    /// Add a line to the timeline.
    /// </summary>
    /// <remarks>
    /// Allowed after resolution, on purpose. Half of every timeline is written during the review
    /// a week later, when somebody remembers the alert that fired at 01:50 and was dismissed —
    /// and a system that closed the timeline when the incident closed would lose exactly the
    /// lines that were worth having.
    /// </remarks>
    public IncidentNote Note(
        string text, NoteKind kind, Guid byId, DateTimeOffset at, DateTimeOffset now)
    {
        var note = new IncidentNote(at, now, byId, text, kind);

        _notes.Add(note);

        return note;
    }

    /// <summary>Put somebody in charge, or hand it over.</summary>
    public void LedBy(Guid leadId, Guid byId, DateTimeOffset now)
    {
        if (LeadId == leadId)
        {
            return;
        }

        LeadId = leadId;

        Written("Taken over", byId, now);
    }

    /// <summary>
    /// Change how bad this is said to be, with the reason.
    /// </summary>
    /// <remarks>
    /// The reason is required and the change is written into the timeline, because the
    /// interesting thing about a severity is never its final value. An incident raised as minor
    /// and raised to critical forty minutes later says something about the firm's alerting that
    /// the same incident, recorded as critical from the start, hides completely.
    /// </remarks>
    public void Reclassify(IncidentSeverity severity, string why, Guid byId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(why))
        {
            throw new ArgumentException(
                "Say why the severity changed. A severity that moved for no recorded reason is "
                + "the thing a review cannot make sense of afterwards.",
                nameof(why));
        }

        if (severity == Severity)
        {
            return;
        }

        var was = Severity;

        Severity = severity;

        Written($"{was} to {severity}: {why.Trim()}", byId, now);
    }

    public void Retitle(string title) => Title = Required(title, nameof(title));

    public void Affecting(Guid? serviceId, string? affects)
    {
        ServiceId = serviceId;
        Affects = Trimmed(affects);
    }

    /// <summary>
    /// Correct when it started, once somebody has looked properly.
    /// </summary>
    /// <remarks>
    /// Refused if it would put the start after the report, because that is not a correction —
    /// it is a typo that would make time-to-detect negative and quietly poison every average
    /// computed from it.
    /// </remarks>
    public void StartedAtActually(DateTimeOffset at, Guid byId, DateTimeOffset now)
    {
        if (at > ReportedAt)
        {
            throw new ArgumentException(
                "It cannot have started after it was reported.", nameof(at));
        }

        if (at == StartedAt)
        {
            return;
        }

        var was = StartedAt;

        StartedAt = at;

        Written(
            $"Start moved from {was:d MMM HH:mm} to {at:d MMM HH:mm}",
            byId,
            now);
    }

    /// <summary>
    /// The harm has stopped.
    /// </summary>
    /// <remarks>
    /// How is required. "Mitigated" on its own is the least useful word in an incident record —
    /// the next person having this incident needs to know that the flag was turned off, not
    /// that somebody felt better.
    /// </remarks>
    public void Mitigate(string how, Guid byId, DateTimeOffset at, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(how))
        {
            throw new ArgumentException("Say what stopped it.", nameof(how));
        }

        if (Status != IncidentStatus.Open)
        {
            throw new InvalidOperationException(
                Status == IncidentStatus.Mitigated
                    ? "This is already mitigated."
                    : "This is resolved, so there is nothing left to mitigate.");
        }

        Status = IncidentStatus.Mitigated;
        MitigatedAt = at;

        Note(how.Trim(), NoteKind.Action, byId, at, now);

        Raise(new IncidentMitigated(Id, Number, Severity, StartedAt, at));
    }

    /// <summary>
    /// The cause is gone.
    /// </summary>
    /// <remarks>
    /// Reachable from open as well as from mitigated, because plenty of incidents are fixed
    /// rather than worked around and forcing a mitigation step first would have people clicking
    /// a button to describe something they did not do. When it happens that way both timestamps
    /// are set to the same moment, which is true: the harm stopped when the cause did.
    /// </remarks>
    public void Resolve(string cause, Guid byId, DateTimeOffset at, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(cause))
        {
            throw new ArgumentException(
                "Say what broke, in one line. The why belongs in the review.", nameof(cause));
        }

        if (Status == IncidentStatus.Resolved)
        {
            throw new InvalidOperationException("This is already resolved.");
        }

        Status = IncidentStatus.Resolved;
        ResolvedAt = at;
        MitigatedAt ??= at;
        Cause = cause.Trim();

        Note($"Resolved: {Cause}", NoteKind.Action, byId, at, now);

        Raise(new IncidentResolved(Id, Number, Severity, StartedAt, MitigatedAt.Value, at));
    }

    /// <summary>
    /// It was not over.
    /// </summary>
    /// <remarks>
    /// Rather than raising a second incident, because it is the same incident: the same start,
    /// the same customers, the same cause. Two records would split the timeline in half and make
    /// both durations wrong. The mitigation and resolution times are cleared because they were
    /// not true, and the timeline keeps every line saying they were once claimed.
    /// </remarks>
    public void Reopen(string why, Guid byId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(why))
        {
            throw new ArgumentException("Say what came back.", nameof(why));
        }

        if (Status == IncidentStatus.Open)
        {
            return;
        }

        Status = IncidentStatus.Open;
        MitigatedAt = null;
        ResolvedAt = null;

        Written($"Reopened: {why.Trim()}", byId, now);
    }

    /// <summary>
    /// Excluded because a title is a summary somebody keeps sharpening.
    /// </summary>
    /// <remarks>
    /// Every other field here is in the trail. The title is not, because it is rewritten three
    /// times during an incident as people work out what is actually wrong, and a trail full of
    /// "Payments slow" to "Payments failing" to "Card payments failing for Kenyan cards" says
    /// nothing that the timeline does not say better.
    /// </remarks>
    public static IReadOnlySet<string> AuditExcludes { get; } =
        new HashSet<string> { nameof(Title) };

    /// <summary>A line the system wrote about itself, at the moment it happened.</summary>
    private void Written(string text, Guid byId, DateTimeOffset now) =>
        Note(text, NoteKind.Decision, byId, now, now);

    private static string Required(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record IncidentRaised(
    Guid IncidentId,
    int Number,
    string Title,
    IncidentSeverity Severity,
    DateTimeOffset StartedAt,
    DateTimeOffset ReportedAt) : DomainEvent;

public sealed record IncidentMitigated(
    Guid IncidentId,
    int Number,
    IncidentSeverity Severity,
    DateTimeOffset StartedAt,
    DateTimeOffset MitigatedAt) : DomainEvent;

/// <summary>
/// An incident is over.
/// </summary>
/// <remarks>
/// Carries both timestamps rather than a duration, so that whoever reads it later can compute
/// whichever of the three intervals they care about rather than the one that seemed important
/// the day this record was written.
/// </remarks>
public sealed record IncidentResolved(
    Guid IncidentId,
    int Number,
    IncidentSeverity Severity,
    DateTimeOffset StartedAt,
    DateTimeOffset MitigatedAt,
    DateTimeOffset ResolvedAt) : DomainEvent;
