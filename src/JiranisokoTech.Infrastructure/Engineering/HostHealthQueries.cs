using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Engineering;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Engineering;

/// <summary>
/// Whether each code host can actually reach us.
/// </summary>
/// <remarks>
/// This exists because of a fault that is invisible to every other screen in the system,
/// and the code that causes it says so in writing. <c>WebhookSecrets</c> matches
/// configuration keys without regard to case, and its own remark explains why: "a secret
/// that is present but spelt differently would leave the endpoint refusing every delivery
/// for a reason nothing on a screen would explain."
///
/// That is exactly what happens, and the case-insensitive match only narrows it — a secret
/// that is missing, empty, or under a provider name with a typo in it produces the same
/// outcome. <c>WebhookInbox.ReceiveAsync</c> refuses the delivery before writing anything:
/// no <c>WebhookDelivery</c> row, and not even a tick on the refusals counter, which is
/// only reached by a request that was signed with the wrong secret rather than one that
/// could not be checked at all. So the host sees 503 on every push and gives up retrying
/// within the day, while the machinery screen shows three queue depths of zero and every
/// job green. Every indicator this firm has says the integration is healthy, because every
/// indicator is built from rows, and this failure writes none.
///
/// The answer is not another counter. It is to report the configuration itself beside what
/// has been heard from it, because the two together are the only thing that distinguishes
/// "quiet because nothing happened" from "quiet because nothing can get in".
/// </remarks>
public sealed class HostHealthQueries(
    AppDbContext database,
    IWebhookSecrets secrets,
    IEnumerable<IGitProvider> adapters,
    IClock clock)
{
    /// <summary>
    /// How long a properly configured host may stay silent before it is worth asking.
    /// </summary>
    /// <remarks>
    /// A fortnight, and the number is a compromise between two things that look identical
    /// from here. A secret that is configured but wrong — rotated on the host and not here,
    /// truncated by a copy and paste, or simply stale — passes every check this class can
    /// make: it is present and non-empty. The delivery is then refused at the signature,
    /// which also returns before a row is written, so the wrong value is exactly as
    /// invisible as the missing one.
    ///
    /// What distinguishes them is time. Nothing can be said after a day, because a small
    /// firm has quiet days; after a fortnight, a watched repository that has sent nothing is
    /// worth a look either way — either nobody is working on it, which somebody should know,
    /// or nothing it sends is getting in.
    /// </remarks>
    public static TimeSpan LongEnoughToAsk { get; } = TimeSpan.FromDays(14);

    /// <summary>
    /// One row per code host worth saying anything about.
    /// </summary>
    /// <remarks>
    /// Not one row per value of the enum. A firm using GitHub would get three permanently
    /// empty rows for hosts it has never touched, and a screen with three rows of nothing
    /// on it is a screen people learn to skip — which is the last thing this one can afford,
    /// because the whole point of it is the row that is wrong.
    ///
    /// So a host appears when it has repositories connected, or a secret configured, or
    /// deliveries on record. Each of those means somebody intended this host to work, and
    /// intent is what makes silence worth reporting.
    /// </remarks>
    public async Task<List<HostHealth>> HostsAsync(CancellationToken cancellationToken = default)
    {
        var watched = await database.Repositories
            .AsNoTracking()
            .GroupBy(repository => repository.Provider)
            .Select(group => new
            {
                Provider = group.Key,
                Connected = group.Count(repository => repository.DisconnectedAt == null),
                Repositories = group.Count(),
            })
            .ToListAsync(cancellationToken);

        var heard = await database.Deliveries
            .AsNoTracking()
            .GroupBy(delivery => delivery.Provider)
            .Select(group => new
            {
                Provider = group.Key,
                At = group.Max(delivery => delivery.ReceivedAt),
            })
            .ToListAsync(cancellationToken);

        var read = adapters.Select(adapter => adapter.Provider).ToHashSet();

        var rows = new List<HostHealth>();

        foreach (var provider in Enum.GetValues<GitProvider>())
        {
            var connected = watched.FirstOrDefault(one => one.Provider == provider);
            var lastHeard = heard.FirstOrDefault(one => one.Provider == provider)?.At;
            var configured = secrets.For(provider) is { Length: > 0 };

            if (connected is null && lastHeard is null && !configured)
            {
                continue;
            }

            rows.Add(new HostHealth(
                provider,
                configured,
                read.Contains(provider),
                connected?.Connected ?? 0,
                lastHeard,
                clock.Now));
        }

        return rows;
    }
}

/// <summary>
/// One code host, and whether anything it sends can get in.
/// </summary>
/// <param name="SecretConfigured">
/// Whether a non-empty secret is configured for this host. Only whether — never the value,
/// never its length, and never a masked few characters of it. A monitoring screen exists to
/// be left open, and a secret is not less disclosed for being shortened.
/// </param>
/// <param name="CanBeRead">
/// Whether an adapter is registered that understands this host's payloads. False with
/// repositories connected is the same class of fault as a missing secret: the endpoint
/// refuses everything and writes nothing.
/// </param>
public sealed record HostHealth(
    GitProvider Provider,
    bool SecretConfigured,
    bool CanBeRead,
    int Connected,
    DateTimeOffset? LastHeardAt,
    DateTimeOffset Now)
{
    /// <summary>
    /// Somebody meant this host to work and nothing it sends can get in.
    /// </summary>
    /// <remarks>
    /// The one state this whole class was written for, and the reason it is a property
    /// rather than left to the screen to assemble from three booleans: a screen that has to
    /// work out what the combination means is a screen that will get it subtly wrong the
    /// next time somebody edits it.
    /// </remarks>
    public bool Shut => Connected > 0 && (!SecretConfigured || !CanBeRead);

    /// <summary>
    /// Configured, reachable, and has never been heard from.
    /// </summary>
    /// <remarks>
    /// Milder than <see cref="Shut"/> and worth separating from it. A host set up minutes
    /// ago has not been heard from either, so this is a prompt to check rather than a
    /// statement that something is broken — most hosts send a test delivery when the
    /// webhook is added, and its absence usually means the webhook was never added.
    /// </remarks>
    public bool NeverHeardFrom => !Shut && Connected > 0 && LastHeardAt is null;

    /// <summary>
    /// Set up as far as anything here can tell, and silent for a fortnight.
    /// </summary>
    /// <remarks>
    /// The half <see cref="Shut"/> cannot reach, and the more likely failure of the two. A
    /// secret that is present but wrong satisfies every test this class can make, and the
    /// delivery it refuses is refused at the signature — before a row is written — so it is
    /// exactly as invisible as no secret at all. Only silence shows it.
    ///
    /// Which is why this is amber and <c>Shut</c> is red, and why the screen says what it
    /// cannot tell apart. A fortnight of quiet on a repository nobody pushed to is not a
    /// fault, and reporting it as one would teach somebody to ignore the colour. What can
    /// honestly be said is that the two look the same from here, and one of them is worth
    /// five minutes.
    /// </remarks>
    public bool QuietTooLong =>
        !Shut
        && Connected > 0
        && LastHeardAt is { } last
        && Now - last > HostHealthQueries.LongEnoughToAsk;

    /// <summary>Anything on this row somebody should look at.</summary>
    public bool WorthALook => Shut || NeverHeardFrom || QuietTooLong;
}
