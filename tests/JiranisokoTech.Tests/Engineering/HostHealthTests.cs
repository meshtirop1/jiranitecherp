using JiranisokoTech.Application.Engineering;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Infrastructure.Engineering;
using JiranisokoTech.Tests.Infrastructure;
using Repository = JiranisokoTech.Domain.Engineering.Repository;

namespace JiranisokoTech.Tests.Engineering;

/// <summary>
/// Whether a code host can reach us, which is the one thing no queue depth can say.
/// </summary>
/// <remarks>
/// The fault these tests exist for is the shape this codebase keeps finding: a failure
/// that writes no row, on a system whose every indicator is built from rows.
/// <c>WebhookInbox.ReceiveAsync</c> refuses a delivery with no configured secret before
/// anything is recorded — no delivery row, and not even the refusals counter, which only a
/// wrongly-signed request reaches. So the host gets 503 on every push and stops retrying
/// within the day, while the machinery screen shows three queue depths of zero and every
/// job green.
///
/// <c>WebhookSecrets</c> already says this in a remark: a secret spelt differently "would
/// leave the endpoint refusing every delivery for a reason nothing on a screen would
/// explain." This is that screen, and these are the states it has to get right.
/// </remarks>
public class HostHealthTests
{
    /// <summary>
    /// Repositories connected and no secret is a closed door, and it is named as one.
    /// </summary>
    /// <remarks>
    /// The whole reason for the class. Nothing else in the system reports this: the
    /// repository is connected, the adapter is registered, the queues are empty because
    /// nothing is getting in, and every other indicator reads healthy.
    /// </remarks>
    [Fact]
    public async Task A_host_with_repositories_and_no_secret_is_shut()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();

        await ConnectAsync(fixture, GitProvider.GitHub);

        var hosts = await ReadAsync(fixture, secrets: new Secrets());

        var host = Assert.Single(hosts);

        Assert.Equal(GitProvider.GitHub, host.Provider);
        Assert.True(host.Shut);
        Assert.False(host.SecretConfigured);
        Assert.True(host.CanBeRead);
        Assert.Equal(1, host.Connected);

        // And not also reported as merely quiet, which would be a softer word for the
        // same row and would let a reader pick the reassuring one.
        Assert.False(host.NeverHeardFrom);
    }

    /// <summary>
    /// An empty secret is no secret. This is what a blank environment variable looks like.
    /// </summary>
    [Fact]
    public async Task A_secret_configured_as_an_empty_string_is_not_configured()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();

        await ConnectAsync(fixture, GitProvider.GitHub);

        var hosts = await ReadAsync(
            fixture, new Secrets { [GitProvider.GitHub] = string.Empty });

        Assert.True(Assert.Single(hosts).Shut);
    }

    /// <summary>
    /// A host nothing can read is the same class of fault as one with no secret.
    /// </summary>
    /// <remarks>
    /// Separate words on the screen, because the two have different fixes — one is a
    /// configuration value and the other is a deployment that shipped without an adapter
    /// registered — but the same consequence, so both count as shut.
    /// </remarks>
    [Fact]
    public async Task A_host_no_adapter_reads_is_shut_too()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();

        await ConnectAsync(fixture, GitProvider.GitLab);

        var hosts = await ReadAsync(
            fixture,
            new Secrets { [GitProvider.GitLab] = "a-real-secret" },
            adapters: []);

        var host = Assert.Single(hosts);

        Assert.True(host.Shut);
        Assert.True(host.SecretConfigured);
        Assert.False(host.CanBeRead);
    }

    /// <summary>
    /// Set up properly and never heard from is a prompt, not a verdict.
    /// </summary>
    /// <remarks>
    /// Kept apart from shut because the two want different reactions and one of them is
    /// often nothing at all — a host connected two minutes ago has not been heard from
    /// either. Reporting both in red would train somebody to dismiss the colour.
    /// </remarks>
    [Fact]
    public async Task A_host_set_up_properly_and_silent_is_not_called_broken()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();

        await ConnectAsync(fixture, GitProvider.GitHub);

        var hosts = await ReadAsync(
            fixture, new Secrets { [GitProvider.GitHub] = "a-real-secret" });

        var host = Assert.Single(hosts);

        Assert.False(host.Shut);
        Assert.True(host.NeverHeardFrom);
        Assert.Null(host.LastHeardAt);
    }

    /// <summary>Heard from, and it says when.</summary>
    [Fact]
    public async Task A_host_that_has_delivered_says_when_it_last_did()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();

        var repositoryId = await ConnectAsync(fixture, GitProvider.GitHub);

        var older = fixture.Clock.Now.AddDays(-3);
        var newer = fixture.Clock.Now.AddHours(-2);

        await using (var write = fixture.NewContext())
        {
            write.Deliveries.Add(WebhookDelivery.Receive(
                GitProvider.GitHub, "one", "push", "{}", repositoryId, older));

            write.Deliveries.Add(WebhookDelivery.Receive(
                GitProvider.GitHub, "two", "push", "{}", repositoryId, newer));

            await write.SaveChangesAsync();
        }

        var hosts = await ReadAsync(
            fixture, new Secrets { [GitProvider.GitHub] = "a-real-secret" });

        var host = Assert.Single(hosts);

        Assert.Equal(newer, host.LastHeardAt);
        Assert.False(host.Shut);
        Assert.False(host.NeverHeardFrom);
    }

    /// <summary>
    /// A host set up properly and silent for a fortnight is marked, and only marked.
    /// </summary>
    /// <remarks>
    /// This is the half <see cref="HostHealth.Shut"/> cannot reach, and it is the likelier
    /// failure of the two. A secret rotated on the host and not here is present and
    /// non-empty, so every test this class can make passes — and the delivery it refuses is
    /// refused at the signature, which returns before a row is written. The wrong value is
    /// exactly as invisible as no value.
    ///
    /// Marked in amber rather than red, and the boundary case below is why. Quiet is not
    /// proof of anything: a firm this size has repositories nobody touches for a month, and
    /// reporting those as broken is how somebody learns to ignore the colour.
    /// </remarks>
    [Fact]
    public async Task A_host_silent_for_a_fortnight_is_marked_and_not_called_broken()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();

        var repositoryId = await ConnectAsync(fixture, GitProvider.GitHub);

        await HeardAsync(
            fixture,
            repositoryId,
            fixture.Clock.Now - HostHealthQueries.LongEnoughToAsk - TimeSpan.FromDays(1));

        var host = Assert.Single(await ReadAsync(
            fixture, new Secrets { [GitProvider.GitHub] = "a-real-secret" }));

        Assert.True(host.QuietTooLong);
        Assert.True(host.WorthALook);

        // And not escalated. Nothing here is evidence of a fault.
        Assert.False(host.Shut);
        Assert.False(host.NeverHeardFrom);
    }

    /// <summary>A quiet week is a quiet week, and says nothing.</summary>
    [Fact]
    public async Task A_host_heard_from_inside_the_window_is_left_alone()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();

        var repositoryId = await ConnectAsync(fixture, GitProvider.GitHub);

        await HeardAsync(
            fixture,
            repositoryId,
            fixture.Clock.Now - HostHealthQueries.LongEnoughToAsk + TimeSpan.FromDays(1));

        var host = Assert.Single(await ReadAsync(
            fixture, new Secrets { [GitProvider.GitHub] = "a-real-secret" }));

        Assert.False(host.QuietTooLong);
        Assert.False(host.WorthALook);
    }

    /// <summary>
    /// A host with no secret is called shut rather than quiet, however long it has been.
    /// </summary>
    /// <remarks>
    /// The two are mutually exclusive on purpose. A row that carried both would offer a
    /// reader the milder of two words for the same problem, and the milder one is the one
    /// that gets deferred.
    /// </remarks>
    [Fact]
    public async Task A_shut_host_is_not_also_reported_as_merely_quiet()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();

        var repositoryId = await ConnectAsync(fixture, GitProvider.GitHub);

        await HeardAsync(fixture, repositoryId, fixture.Clock.Now.AddYears(-1));

        var host = Assert.Single(await ReadAsync(fixture, new Secrets()));

        Assert.True(host.Shut);
        Assert.False(host.QuietTooLong);
    }

    /// <summary>
    /// A host nobody has touched is not listed at all.
    /// </summary>
    /// <remarks>
    /// Four rows, three of them permanently empty, is a screen people learn to skip — and
    /// this is the one screen that cannot afford that, because its whole purpose is the one
    /// row that is wrong. A host earns a row by somebody having intended it to work.
    /// </remarks>
    [Fact]
    public async Task A_host_nobody_has_set_up_is_not_listed()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();

        await ConnectAsync(fixture, GitProvider.GitHub);

        var hosts = await ReadAsync(
            fixture, new Secrets { [GitProvider.GitHub] = "a-real-secret" });

        Assert.Single(hosts);
        Assert.DoesNotContain(hosts, host => host.Provider == GitProvider.Bitbucket);
    }

    /// <summary>
    /// A configured secret with no repositories yet still earns a row.
    /// </summary>
    /// <remarks>
    /// Somebody halfway through setting a host up, and the row is how they see where they
    /// got to. It is not shut, because nothing is being refused — there is nothing to
    /// refuse.
    /// </remarks>
    [Fact]
    public async Task A_configured_host_with_nothing_connected_yet_is_listed_and_not_shut()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();

        var hosts = await ReadAsync(
            fixture, new Secrets { [GitProvider.GitHub] = "a-real-secret" });

        var host = Assert.Single(hosts);

        Assert.Equal(0, host.Connected);
        Assert.False(host.Shut);
        Assert.False(host.NeverHeardFrom);
    }

    /// <summary>
    /// A disconnected repository does not keep a host on the critical list.
    /// </summary>
    /// <remarks>
    /// Otherwise deliberately disconnecting a repository and removing its secret — which is
    /// the correct way to stop watching a host — would leave a red banner on the monitoring
    /// screen forever, with nothing anybody could do to clear it.
    /// </remarks>
    [Fact]
    public async Task A_host_whose_repositories_were_all_disconnected_is_not_shut()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();

        var repositoryId = await ConnectAsync(fixture, GitProvider.GitHub);

        await using (var write = fixture.NewContext())
        {
            var repository = await write.Repositories.FindAsync(repositoryId);
            repository!.Disconnect(fixture.Clock.Now);
            await write.SaveChangesAsync();
        }

        var hosts = await ReadAsync(fixture, new Secrets());

        var host = Assert.Single(hosts);

        Assert.Equal(0, host.Connected);
        Assert.False(host.Shut);
    }

    /// <summary>
    /// The secret itself never leaves the configuration.
    /// </summary>
    /// <remarks>
    /// Asserted over every value the record carries rather than by reading the one property
    /// that would obviously hold it, because the fault this guards against is somebody
    /// adding a helpful "ends with …" to the screen later. This is a page meant to be left
    /// open on a second monitor, and a secret is not less disclosed for being shortened.
    /// </remarks>
    [Fact]
    public async Task Nothing_the_screen_receives_carries_the_secret()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();

        const string secret = "the-value-github-holds";

        await ConnectAsync(fixture, GitProvider.GitHub);

        var hosts = await ReadAsync(fixture, new Secrets { [GitProvider.GitHub] = secret });

        var host = Assert.Single(hosts);

        foreach (var property in host.GetType().GetProperties())
        {
            var value = property.GetValue(host)?.ToString();

            Assert.False(
                value is not null && value.Contains(secret, StringComparison.OrdinalIgnoreCase),
                $"HostHealth.{property.Name} carries the webhook secret to the screen.");
        }

        Assert.DoesNotContain(secret, host.ToString());
    }

    private static async Task<List<HostHealth>> ReadAsync(
        DatabaseFixture fixture, Secrets secrets, IGitProvider[]? adapters = null)
    {
        await using var context = fixture.NewContext();

        // The real adapters rather than stand-ins. What is being asked is whether one is
        // registered for a provider, and a stub would answer that about the stub.
        return await new HostHealthQueries(
            context,
            secrets,
            adapters ?? [new GitHubProvider()],
            fixture.Clock).HostsAsync();
    }

    /// <summary>One delivery from this host, at the given moment.</summary>
    private static async Task HeardAsync(
        DatabaseFixture fixture, Guid repositoryId, DateTimeOffset at)
    {
        await using var write = fixture.NewContext();

        write.Deliveries.Add(WebhookDelivery.Receive(
            GitProvider.GitHub, $"delivery-{at.Ticks}", "push", "{}", repositoryId, at));

        await write.SaveChangesAsync();
    }

    private static async Task<Guid> ConnectAsync(DatabaseFixture fixture, GitProvider provider)
    {
        await using var write = fixture.NewContext();

        var repository = Repository.Connect(
            provider, "jiranisokotech", "erp", null, new string('0', 64), fixture.Clock.Now);

        write.Repositories.Add(repository);
        await write.SaveChangesAsync();

        return repository.Id;
    }

    /// <summary>The configured secrets, as a dictionary rather than as configuration.</summary>
    private sealed class Secrets : Dictionary<GitProvider, string>, IWebhookSecrets
    {
        public string? For(GitProvider provider) =>
            TryGetValue(provider, out var secret) ? secret : null;
    }
}
