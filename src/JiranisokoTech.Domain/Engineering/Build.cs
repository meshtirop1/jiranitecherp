using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Engineering;

/// <summary>
/// How a build ended.
/// </summary>
/// <remarks>
/// Four outcomes, and <see cref="Running"/> is one of them because a build is the
/// first thing in this integration that has a middle. A commit has happened; a
/// build is happening, and the fifteen minutes it takes are exactly the window in
/// which somebody looks at the screen.
///
/// Cancelled is kept apart from Failed deliberately. A cancelled build says
/// nothing about the code — somebody pushed again, or the queue was drained — and
/// counting it as a failure would put a red mark against work that was never
/// broken, which is how people learn to ignore the colour.
/// </remarks>
public enum BuildOutcome
{
    Running = 1,
    Passed = 2,
    Failed = 3,
    Cancelled = 4,

    /// <summary>
    /// Stopped, waiting for a person to say yes.
    /// </summary>
    /// <remarks>
    /// GitLab's manual pipelines and Azure's pending-approval gates both stop and wait. Read
    /// as Running they would show as building for three days; read as Failed they would put a
    /// red mark against code nobody has found fault with. Neither is true, and the difference
    /// matters because the action is different: a failure wants fixing and this wants
    /// somebody pressing a button.
    /// </remarks>
    Blocked = 5,
}

/// <summary>
/// A build of a commit, as the repository's host reported it.
/// </summary>
/// <remarks>
/// Section 13 of the brief asks for CI/CD, and section 12 could not be finished
/// without it: a task could show its branch, its commits and its pull request, and
/// then stopped — so "is it built, did the tests pass" was a question this system
/// could not answer about work it otherwise knew everything about.
///
/// <b>Nothing here runs a build.</b> This is a mirror, like <see cref="Commit"/>
/// and <see cref="PullRequest"/>. The host runs the pipeline and says what
/// happened; this records that it said so. Building a runner into an ERP would be
/// a second CI system for the firm to maintain, worse than the one they have.
///
/// <b>What it deliberately does not hold.</b> No logs, no artifacts, no test
/// counts. The brief's section 13 names tests and artifacts, and the honest
/// position is that a webhook does not carry them: GitHub's workflow_run payload
/// says a run finished and what its conclusion was, and nothing about how many
/// assertions ran. Storing a number this system cannot get would mean inventing
/// one, and an invented figure on a screen about quality is worse than a blank.
/// The link to the host is kept instead, because that is where the log is.
/// </remarks>
public sealed class Build : Entity, IAuditable
{
    private Build()
    {
        ExternalId = string.Empty;
        Name = string.Empty;
        Sha = string.Empty;
        Branch = string.Empty;
    }

    private Build(
        Guid repositoryId,
        string externalId,
        string name,
        string sha,
        string branch,
        Guid? workItemId,
        BuildOutcome outcome,
        DateTimeOffset startedAt,
        DateTimeOffset? finishedAt,
        string? url)
    {
        RepositoryId = repositoryId;
        ExternalId = Required(externalId, nameof(externalId));
        Name = Required(name, nameof(name));
        Sha = Hash(sha, nameof(sha));
        Branch = Required(branch, nameof(branch));
        WorkItemId = workItemId;
        Outcome = outcome;
        StartedAt = startedAt;
        FinishedAt = Settled(outcome) ? finishedAt ?? startedAt : null;
        Url = Trimmed(url);

        Raise(new BuildRecorded(Id, repositoryId, Sha, Branch, workItemId, outcome, startedAt));
    }

    public static Build Record(
        Guid repositoryId,
        string externalId,
        string name,
        string sha,
        string branch,
        Guid? workItemId,
        BuildOutcome outcome,
        DateTimeOffset startedAt,
        DateTimeOffset? finishedAt = null,
        string? url = null) =>
        new(repositoryId, externalId, name, sha, branch, workItemId, outcome, startedAt,
            finishedAt, url);

    public Guid RepositoryId { get; private init; }

    /// <summary>
    /// The host's own identifier for this run, which is what makes a redelivery
    /// harmless.
    /// </summary>
    /// <remarks>
    /// A build arrives at least twice in the ordinary case — once when it starts
    /// and once when it finishes — and GitHub re-sends a delivery whenever it is
    /// unsure the first one landed. Without this, every one of those would be a
    /// new row, and a task would show six builds of one commit.
    ///
    /// A string rather than a number because the four hosts disagree: GitHub
    /// gives an integer, Azure DevOps a GUID.
    /// </remarks>
    public string ExternalId { get; private init; }

    /// <summary>What the pipeline is called, as the repository names it.</summary>
    /// <remarks>
    /// Kept because a repository usually has several — a test run, a lint run, a
    /// container build — and "the build failed" is not actionable until somebody
    /// knows which. It is the host's own name for it, not one chosen here.
    /// </remarks>
    public string Name { get; private init; }

    /// <summary>The commit that was built.</summary>
    public string Sha { get; private init; }

    public string Branch { get; private init; }

    /// <summary>
    /// The work this belongs to.
    /// </summary>
    /// <remarks>
    /// Resolved from the commit rather than by reading the branch name again — see
    /// the note in DeliveryDispatcher. A commit already carries its link, and that
    /// link may have been set by a person on the work item page; re-deriving it
    /// here would quietly overrule them.
    /// </remarks>
    public Guid? WorkItemId { get; private set; }

    public BuildOutcome Outcome { get; private set; }

    public DateTimeOffset StartedAt { get; private init; }

    /// <summary>When it ended, or nothing while it is still running.</summary>
    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>Where the log is, on the host.</summary>
    public string? Url { get; private set; }

    public bool IsRunning => Outcome == BuildOutcome.Running;

    /// <summary>
    /// How long it took, or nothing while it is still going.
    /// </summary>
    /// <remarks>
    /// Derived rather than stored. A duration column and two timestamps is three
    /// places for the same fact, and the day one of them is written without the
    /// others the screen shows a build that took minus four minutes.
    /// </remarks>
    public TimeSpan? Took => FinishedAt is { } finished ? finished - StartedAt : null;

    /// <summary>
    /// The same run, reported again — usually because it finished.
    /// </summary>
    /// <remarks>
    /// Updates rather than refusing, because the second delivery about a run is
    /// the interesting one: the first says a build started, and nobody needs that
    /// on a screen for its own sake.
    ///
    /// A finished build does not go back to running. GitHub re-sends deliveries it
    /// is unsure about, and they arrive in whatever order the network gives them —
    /// so a "queued" delivery redelivered after the "completed" one would
    /// otherwise turn a passed build back into a running one, permanently, because
    /// nothing further is coming to correct it.
    /// </remarks>
    public void Ended(BuildOutcome outcome, DateTimeOffset at, string? url = null)
    {
        /*
         * Settled rather than IsRunning, and the distinction is the whole reason Blocked
         * exists. A pipeline waiting at a manual gate is not running and has not ended, so a
         * guard on IsRunning would refuse the delivery that arrives when somebody finally
         * presses the button — leaving the build showing "waiting on somebody" for ever, with
         * nothing further coming to correct it.
         */
        if (Settled(Outcome))
        {
            return;
        }

        Outcome = outcome;
        FinishedAt = Settled(outcome) ? at : null;
        Url = Trimmed(url) ?? Url;

        if (Settled(outcome))
        {
            Raise(new BuildFinished(Id, RepositoryId, Sha, Branch, WorkItemId, outcome, at));
        }
    }

    /// <summary>Attach it to a piece of work, or detach it.</summary>
    public void Belongs(Guid? workItemId) => WorkItemId = workItemId;

    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    /// <summary>
    /// A commit hash, lower-cased.
    /// </summary>
    /// <remarks>
    /// Because this is what a build or a deployment is matched to a commit by, and the match
    /// is string equality against a case-sensitive unique index. GitHub sends lower-case hex
    /// and Azure DevOps has not always; one host reporting <c>A1B2C3</c> where the push
    /// reported <c>a1b2c3</c> would leave every build on that repository attached to nothing,
    /// with no error and nothing in a log to look at — the panel would simply be empty and
    /// people would conclude the feature did not work.
    ///
    /// Lower-cased here and not in <see cref="Commit"/>, deliberately: commits.Sha carries a
    /// unique index that thousands of rows already satisfy, and normalising it now would be a
    /// data migration to fix a case that has not occurred. This is the newer side of the
    /// join, so it is the side that bends.
    /// </remarks>
    private static string Hash(string value, string parameter) =>
        Required(value, parameter).ToLowerInvariant();

    /// <summary>
    /// Whether this outcome is the end of the run.
    /// </summary>
    /// <remarks>
    /// Not the same question as whether it is <see cref="BuildOutcome.Running"/>. A pipeline
    /// stopped at a manual gate has neither finished nor is it doing anything, and treating
    /// the two as one question is how a blocked build either gets a finish time it never
    /// reached or becomes unsettleable.
    /// </remarks>
    private static bool Settled(BuildOutcome outcome) =>
        outcome is not (BuildOutcome.Running or BuildOutcome.Blocked);

    private static string Required(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record BuildRecorded(
    Guid BuildId,
    Guid RepositoryId,
    string Sha,
    string Branch,
    Guid? WorkItemId,
    BuildOutcome Outcome,
    DateTimeOffset At) : DomainEvent;

/// <summary>
/// A build ended.
/// </summary>
/// <remarks>
/// Raised only on the transition, not on every delivery about the run, so a
/// handler can rely on seeing it once. Whether anything should act on a failure —
/// move the work, tell somebody — is deliberately not decided here; see the note
/// on the handler registry.
/// </remarks>
public sealed record BuildFinished(
    Guid BuildId,
    Guid RepositoryId,
    string Sha,
    string Branch,
    Guid? WorkItemId,
    BuildOutcome Outcome,
    DateTimeOffset At) : DomainEvent;
