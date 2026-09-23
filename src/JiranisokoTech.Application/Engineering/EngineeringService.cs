using System.Security.Cryptography;
using System.Text;
using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.People;
using JiranisokoTech.Application.Work;
using JiranisokoTech.Domain.Engineering;

namespace JiranisokoTech.Application.Engineering;

/// <summary>
/// Connecting repositories, and dealing with deliveries that failed.
/// </summary>
/// <remarks>
/// The deliberate half of the integration — the things a person does — as
/// against <see cref="DeliveryDispatcher"/>, which is the half that happens on
/// its own.
/// </remarks>
public sealed class EngineeringService(
    IEngineeringRepository repositories,
    IWorkRepository work,
    IPeopleRepository people,
    IWebhookSecrets secrets,
    IClock clock)
{
    /// <summary>
    /// Start watching a repository.
    /// </summary>
    /// <remarks>
    /// The secret typed here is checked against the one in configuration and
    /// then thrown away, and only its hash is stored. That check is the point of
    /// the whole exchange: the endpoint verifies signatures against
    /// configuration, so a repository connected with any other secret would be
    /// connected and permanently deaf, and the failure would look like a
    /// provider that had stopped sending rather than a value that was typed
    /// wrong.
    /// </remarks>
    public async Task<Repository> ConnectAsync(
        GitProvider provider,
        string owner,
        string name,
        string secret,
        Guid? projectId = null,
        CancellationToken cancellationToken = default)
    {
        var fullName = $"{owner.Trim()}/{name.Trim()}";

        if (await repositories.ByFullNameAsync(provider, fullName, cancellationToken)
            is { IsWatched: true })
        {
            throw new InvalidOperationException(
                $"{fullName} is already connected. Disconnect it first if you need to change "
                + "how it is set up.");
        }

        if (secrets.For(provider) is not { Length: > 0 } configured)
        {
            throw new InvalidOperationException(
                $"No webhook secret is configured for {provider}. Set "
                + $"Git:Providers:{provider}:Secret "
                + "before connecting a repository, or deliveries will arrive and be refused.");
        }

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(secret.Trim()), Encoding.UTF8.GetBytes(configured)))
        {
            throw new InvalidOperationException(
                "That is not the secret this application is configured with, so deliveries "
                + "signed with it would be refused. Use the same value as "
                + $"Git:Providers:{provider}:Secret.");
        }

        if (projectId is { } project
            && await work.FindProjectAsync(project, cancellationToken) is null)
        {
            throw new InvalidOperationException("That project does not exist.");
        }

        var repository = Repository.Connect(
            provider, owner, name, projectId, Fingerprint(secret.Trim()), clock.Now);

        repositories.Add(repository);
        await repositories.SaveAsync(cancellationToken);

        return repository;
    }

    /// <summary>Stop watching a repository.</summary>
    /// <remarks>
    /// The commits and pull requests already recorded stay. They are the history
    /// of work that was really done, and a repository being archived does not
    /// unmake it.
    /// </remarks>
    public async Task DisconnectAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var repository = await Required(id, cancellationToken);

        repository.Disconnect(clock.Now);
        await repositories.SaveAsync(cancellationToken);
    }

    /// <summary>Point a repository at a different project, or at none.</summary>
    public async Task MoveToProjectAsync(
        Guid id, Guid? projectId, CancellationToken cancellationToken = default)
    {
        var repository = await Required(id, cancellationToken);

        if (projectId is { } project
            && await work.FindProjectAsync(project, cancellationToken) is null)
        {
            throw new InvalidOperationException("That project does not exist.");
        }

        repository.MoveTo(projectId);
        await repositories.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Is this repository's secret still the one the application holds?
    /// </summary>
    /// <remarks>
    /// The tripwire the stored hash exists for. Somebody rotates the secret at
    /// GitHub and updates configuration; every repository connected before that
    /// now holds a hash of the old one, and this is what says so on the screen
    /// instead of leaving a row that looks connected and receives nothing.
    /// </remarks>
    public bool SecretIsCurrent(GitProvider provider, string secretHash) =>
        secrets.For(provider) is { Length: > 0 } configured
        && secretHash == Fingerprint(configured);

    /// <summary>
    /// Put a dead-lettered delivery back in the queue.
    /// </summary>
    /// <remarks>
    /// What the stored body is for. The usual sequence is: deliveries fail on a
    /// payload shape nobody anticipated, somebody fixes the adapter, deploys,
    /// and replays what failed — and the history fills itself in rather than
    /// having a hole in it for the week the bug existed.
    /// </remarks>
    public async Task ReplayAsync(Guid deliveryId, CancellationToken cancellationToken = default)
    {
        var delivery = await repositories.FindDeliveryAsync(deliveryId, cancellationToken)
            ?? throw new InvalidOperationException("That delivery is not recorded.");

        delivery.Replay();
        await repositories.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Say that a provider login is a particular member of staff.
    /// </summary>
    /// <remarks>
    /// Never inferred from a name, however obvious the resemblance. The consequence of
    /// guessing wrong is a person's timesheet showing work somebody else did, and a
    /// login that looks like a name is not evidence that it is one.
    ///
    /// A handle already claimed is reassigned rather than refused, because the
    /// realistic mistake is claiming it for the wrong person and the fix has to be one
    /// action — a delete and a re-add would lose the trail of who held it in between.
    /// </remarks>
    public async Task<Contributor> ClaimAsync(
        GitProvider provider,
        string handle,
        Guid employeeId,
        CancellationToken cancellationToken = default)
    {
        if (await people.FindAsync(employeeId, cancellationToken) is null)
        {
            throw new InvalidOperationException("There is no such member of staff.");
        }

        if (await repositories.ContributorAsync(provider, handle, cancellationToken)
            is { } existing)
        {
            existing.Reassign(employeeId, clock.Now);
            await repositories.SaveAsync(cancellationToken);

            return existing;
        }

        var contributor = Contributor.Claim(provider, handle, employeeId, clock.Now);

        repositories.Add(contributor);
        await repositories.SaveAsync(cancellationToken);

        return contributor;
    }

    /// <summary>Take a claim back.</summary>
    public async Task ReleaseAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (await repositories.FindContributorAsync(id, cancellationToken) is not { } claim)
        {
            return;
        }

        repositories.Remove(claim);
        await repositories.SaveAsync(cancellationToken);
    }

    private async Task<Repository> Required(Guid id, CancellationToken cancellationToken) =>
        await repositories.FindAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("That repository is not connected here.");

    /// <summary>
    /// SHA-256 of the secret, hex.
    /// </summary>
    /// <remarks>
    /// The same reasoning as an API key's fingerprint: a fast hash is right
    /// where the input is a long random value rather than something a person
    /// chose, because there is no dictionary to slow an attacker down with.
    /// </remarks>
    private static string Fingerprint(string secret) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}
