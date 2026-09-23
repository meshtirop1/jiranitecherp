using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Engineering;

/// <summary>
/// Which of the firm's environments something reached.
/// </summary>
/// <remarks>
/// Three, because section 13 of the brief names three and because a firm this size
/// has three. More would be a list somebody maintains instead of deploying.
///
/// <b>Named DeploymentEnvironment and not Environment.</b> A type called
/// <c>Environment</c> in this namespace shadows <c>System.Environment</c> for every
/// file in it, and the failure is silent until somebody writes
/// <c>Environment.GetEnvironmentVariable</c> and gets an error that makes no sense.
/// A namespace called <c>Money</c> did exactly this to the <c>Money</c> type three
/// separate times in this codebase before it was renamed.
///
/// <see cref="Other"/> exists because the mapping from a host's free text is a
/// guess. GitHub's environment is whatever somebody typed into a workflow file, and
/// a firm that deploys to "uat" should see "uat" rather than have it silently filed
/// as staging — which is why <see cref="Deployment.EnvironmentName"/> keeps what
/// was actually said.
/// </remarks>
public enum DeploymentEnvironment
{
    Development = 1,
    Staging = 2,
    Production = 3,

    /// <summary>Somewhere the firm deploys to that is none of the three.</summary>
    Other = 4,
}

/// <summary>
/// How a deployment ended.
/// </summary>
/// <remarks>
/// A deployment has a middle for the same reason a build does, and the middle
/// matters more: "it is going out now" is the state during which somebody watches.
/// </remarks>
public enum DeploymentState
{
    Running = 1,
    Succeeded = 2,
    Failed = 3,
}

/// <summary>
/// Something reached an environment.
/// </summary>
/// <remarks>
/// The other half of what section 12 was missing, and the whole of what section 21
/// still wanted. A task could show its commits and its pull request and could not
/// say whether any of it had actually gone out; and a timesheet could show the
/// day's commits but not that the afternoon was spent getting a release to
/// production.
///
/// <b>It records, and decides nothing.</b> A deployment reaching production does
/// not mark the work delivered. That is a person's decision with its own
/// permission, and a system where a pipeline can close a task is one where an
/// accidental deploy of a stale branch marks three weeks of work done. The same
/// reasoning as an opportunity being won creating no client, no project and no
/// contract: three decisions, three people, three consequences.
///
/// <b>The environment is classified and the original kept.</b> Both, because the
/// enum is what a screen groups by and the text is what somebody typed — and a
/// firm deploying to "prod-eu" deserves to see "prod-eu" on the page while still
/// being counted as production in the total.
/// </remarks>
public sealed class Deployment : Entity, IAuditable
{
    private Deployment()
    {
        ExternalId = string.Empty;
        EnvironmentName = string.Empty;
        Sha = string.Empty;
    }

    private Deployment(
        Guid repositoryId,
        string externalId,
        string environmentName,
        string sha,
        string? branch,
        Guid? workItemId,
        string? deployedBy,
        DeploymentState state,
        DateTimeOffset at,
        string? url)
    {
        RepositoryId = repositoryId;
        ExternalId = Required(externalId, nameof(externalId));
        EnvironmentName = Required(environmentName, nameof(environmentName));
        Environment = Classify(EnvironmentName);
        Sha = Hash(sha, nameof(sha));
        Branch = Trimmed(branch);
        WorkItemId = workItemId;
        DeployedBy = Trimmed(deployedBy);
        State = state;
        At = at;
        FinishedAt = state == DeploymentState.Running ? null : at;
        Url = Trimmed(url);

        Raise(new DeploymentRecorded(
            Id, repositoryId, Environment, EnvironmentName, Sha, workItemId, state, at));
    }

    public static Deployment Record(
        Guid repositoryId,
        string externalId,
        string environmentName,
        string sha,
        string? branch = null,
        Guid? workItemId = null,
        string? deployedBy = null,
        DeploymentState state = DeploymentState.Running,
        DateTimeOffset at = default,
        string? url = null) =>
        new(repositoryId, externalId, environmentName, sha, branch, workItemId, deployedBy,
            state, at, url);

    public Guid RepositoryId { get; private init; }

    /// <summary>
    /// The host's own identifier, which is what makes a redelivery harmless.
    /// </summary>
    /// <remarks>
    /// A deployment reports at least twice — in progress, then success or failure —
    /// and the host re-sends whatever it is unsure of. Without this, a single
    /// release to production would appear on the environments page four times.
    /// </remarks>
    public string ExternalId { get; private init; }

    /// <summary>Which of the firm's environments, as a screen groups them.</summary>
    public DeploymentEnvironment Environment { get; private init; }

    /// <summary>
    /// What the host called it, kept exactly as it came.
    /// </summary>
    /// <remarks>
    /// Because the classification is a guess over free text. Somebody reading
    /// "production" when the workflow said "prod-eu" has been told something
    /// slightly untrue, and on a page about what is live that is the wrong place
    /// to be approximately right.
    /// </remarks>
    public string EnvironmentName { get; private init; }

    /// <summary>The commit that went out.</summary>
    public string Sha { get; private init; }

    /// <summary>
    /// The branch it came from, if the host said.
    /// </summary>
    /// <remarks>
    /// Nullable, unlike a commit's branch, because a deployment is of a commit and
    /// several hosts do not say which ref it was reached through. Recording an empty
    /// string to avoid the null would put a blank in a column somebody filters on.
    /// </remarks>
    public string? Branch { get; private init; }

    /// <summary>The work this belongs to, resolved from the commit.</summary>
    public Guid? WorkItemId { get; private set; }

    /// <summary>Who set it off, as the host names them.</summary>
    public string? DeployedBy { get; private init; }

    public DeploymentState State { get; private set; }

    /// <summary>When it began.</summary>
    public DateTimeOffset At { get; private init; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public string? Url { get; private set; }

    public bool IsRunning => State == DeploymentState.Running;

    /// <summary>Did this one actually land?</summary>
    public bool Live => State == DeploymentState.Succeeded;

    public TimeSpan? Took => FinishedAt is { } finished ? finished - At : null;

    /// <summary>
    /// The same deployment, reported again.
    /// </summary>
    /// <remarks>
    /// Settled once, for the same reason a build is. A redelivered "in progress"
    /// arriving after the success would otherwise turn a live release back into a
    /// running one and leave it there, because nothing further is coming.
    /// </remarks>
    public void Ended(DeploymentState state, DateTimeOffset at, string? url = null)
    {
        if (!IsRunning)
        {
            return;
        }

        State = state;
        FinishedAt = state == DeploymentState.Running ? null : at;
        Url = Trimmed(url) ?? Url;

        if (state != DeploymentState.Running)
        {
            Raise(new DeploymentFinished(
                Id, RepositoryId, Environment, EnvironmentName, Sha, WorkItemId, state, at));
        }
    }

    public void Belongs(Guid? workItemId) => WorkItemId = workItemId;

    /// <summary>
    /// Which environment a host's free text is talking about.
    /// </summary>
    /// <remarks>
    /// Deliberately a short list of prefixes rather than a clever match. "prod"
    /// catches production, prod, prod-eu and production-canary; "stag" and "stage"
    /// catch the usual spellings of staging; dev, test, qa and uat are all somebody
    /// checking something before it is real.
    ///
    /// Anything else is <see cref="DeploymentEnvironment.Other"/> and keeps its
    /// name. Guessing harder would be worse: the cost of filing an unknown
    /// environment as Other is a row in a group called Other, and the cost of
    /// guessing it into Production is a screen claiming something is live when it
    /// is not.
    /// </remarks>
    public static DeploymentEnvironment Classify(string environmentName)
    {
        var name = environmentName.Trim().ToLowerInvariant();

        if (name.StartsWith("prod", StringComparison.Ordinal)
            || name.StartsWith("live", StringComparison.Ordinal))
        {
            return DeploymentEnvironment.Production;
        }

        if (name.StartsWith("stag", StringComparison.Ordinal)
            || name.StartsWith("stage", StringComparison.Ordinal)
            || name.StartsWith("pre-prod", StringComparison.Ordinal)
            || name.StartsWith("preprod", StringComparison.Ordinal))
        {
            return DeploymentEnvironment.Staging;
        }

        if (name.StartsWith("dev", StringComparison.Ordinal)
            || name.StartsWith("test", StringComparison.Ordinal)
            || name.StartsWith("qa", StringComparison.Ordinal)
            || name.StartsWith("uat", StringComparison.Ordinal))
        {
            return DeploymentEnvironment.Development;
        }

        return DeploymentEnvironment.Other;
    }

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

    private static string Required(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record DeploymentRecorded(
    Guid DeploymentId,
    Guid RepositoryId,
    DeploymentEnvironment Environment,
    string EnvironmentName,
    string Sha,
    Guid? WorkItemId,
    DeploymentState State,
    DateTimeOffset At) : DomainEvent;

/// <summary>
/// A deployment finished, one way or the other.
/// </summary>
/// <remarks>
/// Raised once, on the transition. Nothing in this system acts on it yet, and that
/// is the decision rather than an omission — see the remarks on
/// <see cref="Deployment"/> for why a deployment does not move a task.
/// </remarks>
public sealed record DeploymentFinished(
    Guid DeploymentId,
    Guid RepositoryId,
    DeploymentEnvironment Environment,
    string EnvironmentName,
    string Sha,
    Guid? WorkItemId,
    DeploymentState State,
    DateTimeOffset At) : DomainEvent;
