using JiranisokoTech.Domain.Engineering;

namespace JiranisokoTech.Application.Engineering;

/// <summary>
/// What a delivery turned out to be saying, once a provider's adapter read it.
/// </summary>
/// <remarks>
/// The seam between "a provider sent us some JSON" and "something happened in a
/// repository". GitHub, GitLab and Bitbucket describe the same four or five
/// events in three incompatible shapes, and without a type like this the
/// difference between them leaks into the code that records commits — which is
/// how an integration ends up being written once per provider.
///
/// Deliberately small. Everything listed here is something the rest of the
/// system has a use for; a provider's payload carries a great deal more, and
/// the raw body is kept on the delivery for anybody who needs it.
/// </remarks>
public abstract record GitEvent
{
    private GitEvent()
    {
    }

    /// <summary>Somebody pushed commits to a branch.</summary>
    public sealed record Pushed(string Branch, IReadOnlyList<PushedCommit> Commits) : GitEvent;

    /// <summary>A pull request was opened, retitled, merged or closed.</summary>
    /// <remarks>
    /// One case rather than four, because the provider sends one event type with
    /// an action inside it, and because all four carry the same body. Which of
    /// them it is is <see cref="PullRequestChange.Action"/>.
    /// </remarks>
    public sealed record PullRequestChanged(PullRequestChange Change) : GitEvent;

    /// <summary>A reviewer said something.</summary>
    public sealed record Reviewed(
        int Number,
        string ExternalId,
        string Reviewer,
        ReviewVerdict Verdict) : GitEvent;

    /// <summary>A build of a commit started, or ended.</summary>
    /// <remarks>
    /// One case for both, like <see cref="PullRequestChanged"/> and for the same
    /// reason: the host sends one event type with a status inside it, and both
    /// deliveries carry the same body. Which it is, is
    /// <see cref="BuildReport.Outcome"/>.
    /// </remarks>
    public sealed record Built(BuildReport Report) : GitEvent;

    /// <summary>Something reached an environment, or failed to.</summary>
    public sealed record Deployed(DeploymentReport Report) : GitEvent;

    /// <summary>
    /// Read successfully, and there is nothing here for this system.
    /// </summary>
    /// <remarks>
    /// Not an error, and the distinction is the reason this case exists. A
    /// repository that is starred, forked, or has a label renamed sends a
    /// delivery, and treating those as failures would fill the failure list
    /// with things nobody needs to act on until nobody reads it at all.
    /// </remarks>
    public sealed record Uninteresting(string Why) : GitEvent;
}

/// <summary>One commit out of a push.</summary>
public sealed record PushedCommit(
    string Sha,
    string Message,
    string Author,
    DateTimeOffset At);

/// <summary>What happened to a pull request.</summary>
public enum PullRequestAction
{
    Opened = 1,
    Updated = 2,
    Merged = 3,
    Closed = 4,
}

/// <summary>A pull request, as a delivery described it.</summary>
/// <remarks>
/// Carries the fields needed to create the mirror as well as to update it,
/// because a merge can arrive for a pull request this system never saw opened —
/// the webhook was added late, or the delivery that opened it dead-lettered.
/// Refusing to record it then would leave the most interesting half of the
/// history permanently missing.
/// </remarks>
public sealed record PullRequestChange(
    PullRequestAction Action,
    int Number,
    string Title,
    string Branch,
    string Author,
    DateTimeOffset At);

/// <summary>
/// A build, as a delivery described it.
/// </summary>
/// <remarks>
/// Carries everything needed to create the record as well as to settle it, because
/// the delivery that says a build finished can arrive for a run this system never
/// saw start — the webhook was added mid-run, or the first delivery dead-lettered.
/// Refusing it then would lose the only half anybody cares about.
///
/// The branch is required here and nullable on a deployment, and the asymmetry is
/// the hosts': a CI run always knows the ref it was triggered by, and a deployment
/// is of a commit and often does not.
/// </remarks>
public sealed record BuildReport(
    string ExternalId,
    string Name,
    string Sha,
    string Branch,
    BuildOutcome Outcome,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? Url);

/// <summary>A deployment, as a delivery described it.</summary>
/// <remarks>
/// <see cref="EnvironmentName"/> is the host's free text and is not classified
/// here. Classifying is a domain rule — see Deployment.Classify — and doing it in a
/// provider adapter would put four copies of it in the codebase, one per host,
/// which is how three of them come to disagree about whether "uat" is staging.
/// </remarks>
public sealed record DeploymentReport(
    string ExternalId,
    string EnvironmentName,
    string Sha,
    string? Branch,
    string? DeployedBy,
    DeploymentState State,
    DateTimeOffset At,
    string? Url);
