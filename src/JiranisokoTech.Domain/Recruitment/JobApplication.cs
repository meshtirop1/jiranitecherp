using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Recruitment;

/// <summary>Where somebody has got to in a hiring process.</summary>
public enum ApplicationStatus
{
    /// <summary>Arrived. Nobody has looked yet.</summary>
    Received = 1,

    /// <summary>Being read.</summary>
    Screening = 2,

    /// <summary>Through to interviews.</summary>
    Interviewing = 3,

    /// <summary>An offer is out.</summary>
    Offered = 4,

    Hired = 5,

    /// <summary>Not taken forward. The reason is internal.</summary>
    Rejected = 6,

    /// <summary>They withdrew.</summary>
    Withdrawn = 7,
}

/// <summary>
/// Somebody who has applied for a post.
/// </summary>
/// <remarks>
/// Called a job application rather than an application, because
/// <c>Application</c> is a whole layer of this codebase and a domain class of
/// that name makes every using-directive an argument.
///
/// The status is a state machine for the same reason work items have one: a
/// free column is how somebody is marked hired without an offer, or rejected
/// and then quietly moved on again.
/// </remarks>
public sealed class JobApplication : Entity, IAuditable
{
    /// <summary>
    /// What may follow what.
    /// </summary>
    /// <remarks>
    /// Rejection and withdrawal are reachable from every live state, because
    /// both happen at any point and a process that refuses to record them is
    /// one people work around in a spreadsheet.
    ///
    /// Nothing leaves Hired or Withdrawn. A rejection can be reversed — people
    /// are reconsidered, and refusing to allow it means a second application
    /// that loses the history of the first.
    /// </remarks>
    private static readonly Dictionary<ApplicationStatus, ApplicationStatus[]> Allowed = new()
    {
        [ApplicationStatus.Received] =
        [
            ApplicationStatus.Screening, ApplicationStatus.Rejected, ApplicationStatus.Withdrawn,
        ],
        [ApplicationStatus.Screening] =
        [
            ApplicationStatus.Interviewing, ApplicationStatus.Rejected,
            ApplicationStatus.Withdrawn,
        ],
        [ApplicationStatus.Interviewing] =
        [
            ApplicationStatus.Offered, ApplicationStatus.Rejected, ApplicationStatus.Withdrawn,
        ],
        [ApplicationStatus.Offered] =
        [
            ApplicationStatus.Hired, ApplicationStatus.Rejected, ApplicationStatus.Withdrawn,
        ],
        [ApplicationStatus.Rejected] = [ApplicationStatus.Screening],
        [ApplicationStatus.Hired] = [],
        [ApplicationStatus.Withdrawn] = [],
    };

    private JobApplication()
    {
    }

    private JobApplication(Guid postingId, Guid candidateId, DateTimeOffset at, string? note)
    {
        PostingId = postingId;
        CandidateId = candidateId;
        AppliedAt = at;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        Status = ApplicationStatus.Received;

        Raise(new ApplicationReceived(Id, postingId, candidateId, at));
    }

    public static JobApplication Receive(
        Guid postingId, Guid candidateId, DateTimeOffset at, string? note = null) =>
        new(postingId, candidateId, at, note);

    public Guid PostingId { get; private init; }

    public Guid CandidateId { get; private init; }

    public DateTimeOffset AppliedAt { get; private init; }

    public ApplicationStatus Status { get; private set; }

    /// <summary>What they said when they applied.</summary>
    public string? Note { get; private set; }

    /// <summary>
    /// Why they were not taken forward.
    /// </summary>
    /// <remarks>
    /// Internal, and it must never reach the candidate. It is written for
    /// colleagues — "weak on the database side", "we went with somebody more
    /// senior" — and those are notes about a person, not feedback to them.
    ///
    /// The system this replaces sent a rejection email that deliberately left
    /// this out, and that decision is repeated here: any message to a candidate
    /// is built from the status alone. If somebody wants to give real feedback,
    /// they write it themselves, to that person, on purpose.
    /// </remarks>
    public string? RejectionReason { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    /// <summary>What the candidate called their CV.</summary>
    /// <remarks>
    /// Shown to whoever downloads it, and never used to find the file. It is a
    /// string a stranger chose.
    /// </remarks>
    public string? CvFileName { get; private set; }

    /// <summary>What it is stored as, which this system chose.</summary>
    public string? CvStoredName { get; private set; }

    public bool HasCv => CvStoredName is not null;

    /// <summary>
    /// Attach the CV that came with the application.
    /// </summary>
    /// <remarks>
    /// Once only. A second upload against the same application would leave the
    /// first file on disk with nothing pointing at it, and replacing somebody
    /// CV after they applied is not a thing this system offers anybody.
    /// </remarks>
    public void AttachCv(string originalName, string storedName)
    {
        if (CvStoredName is not null)
        {
            throw new InvalidOperationException("This application already has a CV.");
        }

        CvFileName = originalName.Trim();
        CvStoredName = storedName;
    }

    /// <summary>Still in the running.</summary>
    public bool IsLive => Status
        is ApplicationStatus.Received
        or ApplicationStatus.Screening
        or ApplicationStatus.Interviewing
        or ApplicationStatus.Offered;

    /// <summary>Where an application in this state may go next.</summary>
    public static IReadOnlyList<ApplicationStatus> NextFrom(ApplicationStatus status) =>
        Allowed[status];

    public void MoveTo(ApplicationStatus status, DateTimeOffset at, string? because = null)
    {
        if (Status == status)
        {
            return;
        }

        if (!Allowed[Status].Contains(status))
        {
            throw new InvalidOperationException(
                $"An application cannot go from {Say(Status)} to {Say(status)}."
                + (Status is ApplicationStatus.Hired or ApplicationStatus.Withdrawn
                    ? $" {Say(Status)} is where it ends."
                    : string.Empty));
        }

        if (status == ApplicationStatus.Rejected && string.IsNullOrWhiteSpace(because))
        {
            /*
             * A rejection with no reason is one nobody can answer for later —
             * and "why did we not take them?" is asked, by the candidate, by a
             * colleague, and occasionally by a tribunal.
             */
            throw new ArgumentException(
                "Say why they were not taken forward. It stays internal, but somebody will ask.",
                nameof(because));
        }

        var from = Status;
        Status = status;

        RejectionReason = status == ApplicationStatus.Rejected ? because!.Trim() : null;

        if (status is ApplicationStatus.Hired or ApplicationStatus.Rejected
            or ApplicationStatus.Withdrawn)
        {
            DecidedAt = at;
        }

        Raise(new ApplicationMoved(Id, PostingId, CandidateId, from, status, at));

        if (status == ApplicationStatus.Hired)
        {
            // Its own event: far more things care about a hire than about any
            // other move, and making them all filter a status enum is how a
            // subscriber comes to react to the wrong one.
            Raise(new CandidateHired(Id, PostingId, CandidateId, at));
        }
    }

    /// <summary>Nothing is withheld from the trail; the reason belongs in it.</summary>
    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private static string Say(ApplicationStatus status) => status.ToString().ToLowerInvariant();
}

/// <summary>
/// Somebody who has applied for something, once.
/// </summary>
/// <remarks>
/// One row per person rather than per application, so that somebody who applies
/// twice is recognisably the same person. The email address is what identifies
/// them, because it is the only thing they are guaranteed to give.
/// </remarks>
public sealed class Candidate : Entity, IAuditable
{
    private Candidate()
    {
        FullName = string.Empty;
        Email = string.Empty;
    }

    private Candidate(string fullName, string email, string? phone, DateTimeOffset at)
    {
        FullName = Require(fullName, nameof(fullName));
        Email = Require(email, nameof(email)).ToLowerInvariant();
        Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
        FirstSeenAt = at;
    }

    public static Candidate Of(string fullName, string email, string? phone, DateTimeOffset at) =>
        new(fullName, email, phone, at);

    public string FullName { get; private set; }

    /// <summary>Lower-cased, because it is what identifies them.</summary>
    public string Email { get; private init; }

    public string? Phone { get; private set; }

    public DateTimeOffset FirstSeenAt { get; private init; }

    public void Update(string fullName, string? phone)
    {
        FullName = Require(fullName, nameof(fullName));
        Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
    }

    /// <summary>
    /// A candidate is a person outside the firm, and the trail is read inside
    /// it.
    /// </summary>
    /// <remarks>
    /// The phone number is left out. Nobody administering this system needs a
    /// stranger's mobile number in an audit row, and the trail is read by more
    /// people than the recruitment screens are.
    /// </remarks>
    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>
    {
        nameof(Phone),
    };

    private static string Require(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();
}
