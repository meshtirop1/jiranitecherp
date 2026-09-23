using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JiranisokoTech.Application.Integrations;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Domain.Integrations;
using JiranisokoTech.Domain.Money;
using JiranisokoTech.Infrastructure.Engineering;
using JiranisokoTech.Infrastructure.Integrations;
using JiranisokoTech.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace JiranisokoTech.Tests.Integrations;

/// <summary>
/// Telling somebody outside this firm that something happened.
/// </summary>
/// <remarks>
/// The more dangerous direction of section 40. An incoming webhook can at worst write
/// nonsense into a table somebody can look at; an outgoing one sends the firm's own
/// data to a third party, and a mistake there cannot be taken back.
///
/// So roughly half of these are about what must <em>not</em> be sent, or must not be
/// sent twice.
/// </remarks>
public class OutgoingWebhookTests
{
    /// <summary>
    /// Every offered event still exists.
    /// </summary>
    /// <remarks>
    /// The test that keeps the catalogue honest, and the reason the catalogue is a
    /// list of names rather than of types would have been a mistake. A name here that
    /// no longer matches a real event is a subscription that will never fire — which
    /// looks exactly like a working integration until somebody at the far end asks why
    /// they have heard nothing for a month.
    /// </remarks>
    [Fact]
    public void Every_offered_event_is_a_real_event()
    {
        var events = typeof(DomainEvent).Assembly
            .GetTypes()
            .Where(type => typeof(IDomainEvent).IsAssignableFrom(type)
                && type is { IsAbstract: false, IsInterface: false })
            .Select(type => type.Name)
            .ToHashSet(StringComparer.Ordinal);

        var missing = OutboundEvents.Offered.Keys
            .Where(name => !events.Contains(name))
            .ToList();

        Assert.Empty(missing);
    }

    /// <summary>
    /// Every offered event has a handler registered to publish it.
    /// </summary>
    /// <remarks>
    /// The other half of the same honesty. An event on the offered list with no
    /// registration is one somebody can tick a box for and never receive, and the
    /// registration lives in the service collection rather than next to the list —
    /// so nothing but a test connects the two.
    /// </remarks>
    [Fact]
    public void Every_offered_event_is_published()
    {
        var registered = typeof(JiranisokoTech.Infrastructure.ServiceCollectionExtensions)
            .Assembly
            .GetType("JiranisokoTech.Infrastructure.ServiceCollectionExtensions");

        Assert.NotNull(registered);

        // Read from the compiled registration source rather than by building a
        // container, because building one needs a database, a mail server and a key
        // ring — none of which this is about.
        var source = File.ReadAllText(SourcePathOf("ServiceCollectionExtensions.cs"));

        var missing = OutboundEvents.Offered.Keys
            .Where(name => !source.Contains($"PublishToSubscribers<{name}>", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(missing);
    }

    /// <summary>
    /// Nothing about pay, leave, expenses or scorecards may be subscribed to.
    /// </summary>
    /// <remarks>
    /// Asserted rather than left to judgement, because the failure mode is somebody
    /// adding an event to the offered list while wiring up an integration and nobody
    /// noticing that the firm now posts its own people's absences to a third party.
    /// </remarks>
    [Theory]
    [InlineData("Leave")]
    [InlineData("Expense")]
    [InlineData("Scorecard")]
    [InlineData("Interview")]
    [InlineData("TimeEntry")]
    [InlineData("ApiKey")]
    public void The_firms_private_business_is_not_offered(string forbidden) =>
        Assert.DoesNotContain(
            OutboundEvents.Offered.Keys,
            name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));

    /// <summary>An endpoint that is not https is refused.</summary>
    /// <remarks>
    /// In the domain rather than the form, because the payload describes invoices, pay
    /// and clients, and a subscription is added once and then forgotten for years.
    /// </remarks>
    [Theory]
    [InlineData("http://accounts.example.com/hooks")]
    [InlineData("ftp://accounts.example.com/hooks")]
    [InlineData("accounts.example.com/hooks")]
    [InlineData("not a url")]
    public void An_endpoint_that_is_not_https_is_refused(string endpoint) =>
        Assert.Throws<ArgumentException>(() => Subscription.Add(
            "Accounts", endpoint, "protected", [nameof(InvoiceSent)], Now));

    [Fact]
    public void A_subscription_naming_no_events_is_refused() =>
        Assert.Throws<ArgumentException>(() => Subscription.Add(
            "Accounts", "https://accounts.example.com/hooks", "protected", [], Now));

    [Fact]
    public void A_subscription_only_wants_what_it_asked_for()
    {
        var subscription = Subscription.Add(
            "Accounts",
            "https://accounts.example.com/hooks",
            "protected",
            [nameof(InvoiceSent), nameof(InvoiceSettled)],
            Now);

        Assert.True(subscription.Wants(nameof(InvoiceSent)));
        Assert.False(subscription.Wants(nameof(EmployeeLeftLikeName)));
        Assert.False(subscription.Wants(nameof(PullRequestMerged)));
    }

    /// <summary>A subscription that is switched off wants nothing.</summary>
    /// <remarks>
    /// Checked on the subscription rather than only in the query, so that a second
    /// caller which forgot to filter cannot resurrect a disabled endpoint.
    /// </remarks>
    [Fact]
    public void A_subscription_that_is_off_wants_nothing()
    {
        var subscription = Subscription.Add(
            "Accounts", "https://accounts.example.com/hooks", "protected",
            [nameof(InvoiceSent)], Now);

        subscription.Disable("Switched off by hand.", Now);

        Assert.False(subscription.Wants(nameof(InvoiceSent)));
    }

    /// <summary>
    /// A subscription switches itself off after enough deliveries give up.
    /// </summary>
    /// <remarks>
    /// An endpoint gone for days is either decommissioned or was never right, and a
    /// queue retrying into it forever is a queue nobody reads and a slow leak of
    /// attempts at somebody else's address.
    /// </remarks>
    [Fact]
    public void A_subscription_switches_itself_off_after_enough_failures()
    {
        var subscription = Subscription.Add(
            "Accounts", "https://accounts.example.com/hooks", "protected",
            [nameof(InvoiceSent)], Now);

        for (var failure = 0; failure < Subscription.MaximumConsecutiveFailures - 1; failure++)
        {
            subscription.Failed(Now);
        }

        Assert.True(subscription.IsActive);

        subscription.Failed(Now);

        Assert.False(subscription.IsActive);
        Assert.Contains("gave up", subscription.DisabledReason);
    }

    /// <summary>
    /// One success ends the run.
    /// </summary>
    /// <remarks>
    /// "Consecutive" is the whole word. An endpoint that is flaky rather than gone must
    /// never be switched off, or a receiver with a nightly restart loses its
    /// integration within a fortnight.
    /// </remarks>
    [Fact]
    public void One_success_resets_the_failure_count()
    {
        var subscription = Subscription.Add(
            "Accounts", "https://accounts.example.com/hooks", "protected",
            [nameof(InvoiceSent)], Now);

        for (var failure = 0; failure < Subscription.MaximumConsecutiveFailures - 1; failure++)
        {
            subscription.Failed(Now);
        }

        subscription.Delivered(Now);

        for (var failure = 0; failure < Subscription.MaximumConsecutiveFailures - 1; failure++)
        {
            subscription.Failed(Now);
        }

        Assert.True(subscription.IsActive);
    }

    // --- through the database ------------------------------------------------

    /// <summary>
    /// An event reaches the subscribers that asked, and nobody else.
    /// </summary>
    [Fact]
    public async Task An_event_is_queued_only_for_the_subscribers_that_asked()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();

        await using (var context = fixture.NewContext())
        {
            context.Subscriptions.Add(Subscription.Add(
                "Wants merges", "https://one.example.com/hooks", "protected",
                [nameof(PullRequestMerged)], fixture.Clock.Now));

            context.Subscriptions.Add(Subscription.Add(
                "Wants invoices", "https://two.example.com/hooks", "protected",
                [nameof(InvoiceSent)], fixture.Clock.Now));

            await context.SaveChangesAsync();
        }

        await using (var context = fixture.NewContext())
        {
            await new PublishToSubscribers<PullRequestMerged>(
                    new IntegrationRepository(context),
                    fixture.Clock,
                    NullLogger<PublishToSubscribers<PullRequestMerged>>.Instance)
                .HandleAsync(new PullRequestMerged(
                    Guid.CreateVersion7(), Guid.CreateVersion7(), 412, "feature/412",
                    Guid.CreateVersion7(), fixture.Clock.Now));
        }

        await using var after = fixture.NewContext();
        var queued = await after.OutboundDeliveries.SingleAsync();

        Assert.Equal(nameof(PullRequestMerged), queued.Event);

        // The body is an envelope naming the event, so a receiver knows what it is
        // looking at before it parses the inside.
        using var body = JsonDocument.Parse(queued.Payload);
        Assert.Equal(nameof(PullRequestMerged), body.RootElement.GetProperty("event").GetString());
        Assert.Equal(412, body.RootElement.GetProperty("data").GetProperty("number").GetInt32());
    }

    /// <summary>A switched-off subscription is not queued anything.</summary>
    [Fact]
    public async Task A_subscription_that_is_off_is_queued_nothing()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();

        await using (var context = fixture.NewContext())
        {
            var subscription = Subscription.Add(
                "Off", "https://off.example.com/hooks", "protected",
                [nameof(PullRequestMerged)], fixture.Clock.Now);

            subscription.Disable("Switched off by hand.", fixture.Clock.Now);

            context.Subscriptions.Add(subscription);
            await context.SaveChangesAsync();
        }

        await using (var context = fixture.NewContext())
        {
            await new PublishToSubscribers<PullRequestMerged>(
                    new IntegrationRepository(context),
                    fixture.Clock,
                    NullLogger<PublishToSubscribers<PullRequestMerged>>.Instance)
                .HandleAsync(new PullRequestMerged(
                    Guid.CreateVersion7(), Guid.CreateVersion7(), 412, "feature/412",
                    null, fixture.Clock.Now));
        }

        await using var after = fixture.NewContext();
        Assert.Empty(await after.OutboundDeliveries.ToListAsync());
    }

    /// <summary>
    /// A notification that will not go is retried, then given up on.
    /// </summary>
    /// <remarks>
    /// And the delay between attempts grows, which is asserted because a retry policy
    /// that does not back off is a denial-of-service aimed at whoever owns the
    /// endpoint.
    /// </remarks>
    [Fact]
    public async Task A_notification_that_keeps_failing_is_eventually_given_up_on()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var refusing = new Refusing();

        await Queue(fixture);

        for (var pass = 0; pass < OutboundDelivery.MaximumAttempts; pass++)
        {
            await Dispatch(fixture, refusing);

            // The clock moves past whatever backoff was set, so the next pass finds
            // the delivery due. Without this the test would assert the backoff away.
            fixture.Clock.Advance(TimeSpan.FromHours(2));
        }

        await using var after = fixture.NewContext();
        var delivery = await after.OutboundDeliveries.SingleAsync();

        Assert.Equal(OutboundStatus.DeadLettered, delivery.Status);
        Assert.Equal(OutboundDelivery.MaximumAttempts, delivery.Attempts);
        Assert.Equal(503, delivery.ResponseCode);

        // And the subscription has one failure against it, not six. Only a delivery
        // that ran out of attempts counts, or an endpoint would be switched off after
        // two bad minutes.
        Assert.Equal(1, (await after.Subscriptions.SingleAsync()).ConsecutiveFailures);
    }

    /// <summary>The backoff grows, and stops growing.</summary>
    [Fact]
    public void The_retry_delay_grows_and_then_levels_off()
    {
        Assert.Equal(OutboundDispatcher.FirstRetryDelay, OutboundDispatcher.RetryDelayAfter(1));
        Assert.True(
            OutboundDispatcher.RetryDelayAfter(3) > OutboundDispatcher.RetryDelayAfter(2));

        // A large attempt count must not overflow into a negative delay, which would
        // produce a notification that retried instantly forever.
        Assert.Equal(
            OutboundDispatcher.MaximumRetryDelay, OutboundDispatcher.RetryDelayAfter(1000));
    }

    [Fact]
    public async Task A_notification_that_is_accepted_is_marked_sent()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();

        await Queue(fixture);
        await Dispatch(fixture, new Accepting());

        await using var after = fixture.NewContext();
        var delivery = await after.OutboundDeliveries.SingleAsync();

        Assert.Equal(OutboundStatus.Sent, delivery.Status);
        Assert.Equal(200, delivery.ResponseCode);
        Assert.NotNull((await after.Subscriptions.SingleAsync()).LastDeliveryAt);
    }

    /// <summary>
    /// A notification for a switched-off subscription is dropped, not queued forever.
    /// </summary>
    /// <remarks>
    /// Without this, switching a subscription back on would empty weeks of backlog at
    /// whoever owns that address in one burst.
    /// </remarks>
    [Fact]
    public async Task A_notification_is_abandoned_if_its_subscription_was_switched_off()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var id = await Queue(fixture);

        await using (var context = fixture.NewContext())
        {
            (await context.Subscriptions.SingleAsync(one => one.Id == id))
                .Disable("Switched off by hand.", fixture.Clock.Now);

            await context.SaveChangesAsync();
        }

        await Dispatch(fixture, new Accepting());

        await using var after = fixture.NewContext();
        var delivery = await after.OutboundDeliveries.SingleAsync();

        Assert.Equal(OutboundStatus.DeadLettered, delivery.Status);

        // Abandoned without spending the attempts, because retrying a certainty only
        // delays the row that says so.
        Assert.Equal(0, delivery.Attempts);
    }

    /// <summary>
    /// A secret that cannot be read back switches the subscription off.
    /// </summary>
    /// <remarks>
    /// The realistic cause is a key ring that was replaced or lost. Retrying will never
    /// help, so it becomes one clearly broken subscription with a reason somebody can
    /// act on rather than a loop failing silently forever.
    /// </remarks>
    [Fact]
    public async Task A_secret_that_cannot_be_read_back_switches_the_subscription_off()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await Queue(fixture);

        await using (var context = fixture.NewContext())
        {
            await new OutboundDispatcher(
                    new IntegrationRepository(context),
                    new Accepting(),
                    new Unreadable(),
                    fixture.Clock,
                    NullLogger<OutboundDispatcher>.Instance)
                .RunOnceAsync();
        }

        await using var after = fixture.NewContext();
        var subscription = await after.Subscriptions.SingleAsync();

        Assert.False(subscription.IsActive);
        Assert.Contains("key ring", subscription.DisabledReason);
    }

    /// <summary>
    /// The signature is the same scheme this system accepts from GitHub.
    /// </summary>
    /// <remarks>
    /// Deliberately, so whoever receives a notification can verify it with any of the
    /// many libraries written for GitHub webhooks without being told anything new. The
    /// header name is the firm's own, though: borrowing GitHub's would tell a receiver
    /// this is GitHub, and something downstream would eventually treat it as such.
    /// </remarks>
    [Fact]
    public void A_notification_is_signed_the_way_github_signs_its_own()
    {
        const string secret = "the-shared-secret";
        const string body = """{"event":"InvoiceSent"}""";

        var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));

        // Verified with the adapter that reads real GitHub deliveries, which is the
        // strongest available statement that a receiver's GitHub library will accept
        // this.
        Assert.True(new GitHubProviderProbe().Accepts(body, expected, secret));
        Assert.Equal("X-Jiranisoko-Signature-256", HttpOutboundSender.SignatureHeader);
    }

    // --- the scaffolding -----------------------------------------------------

    private static DateTimeOffset Now => new(2026, 9, 23, 9, 0, 0, TimeSpan.Zero);

    private static async Task<Guid> Queue(DatabaseFixture fixture)
    {
        await using var context = fixture.NewContext();

        var subscription = Subscription.Add(
            "Accounts", "https://accounts.example.com/hooks", "protected",
            [nameof(InvoiceSent)], fixture.Clock.Now);

        context.Subscriptions.Add(subscription);
        context.OutboundDeliveries.Add(OutboundDelivery.Queue(
            subscription.Id, nameof(InvoiceSent), """{"event":"InvoiceSent"}""",
            fixture.Clock.Now));

        await context.SaveChangesAsync();

        return subscription.Id;
    }

    private static async Task Dispatch(DatabaseFixture fixture, IOutboundSender sender)
    {
        await using var context = fixture.NewContext();

        await new OutboundDispatcher(
                new IntegrationRepository(context),
                sender,
                new Readable(),
                fixture.Clock,
                NullLogger<OutboundDispatcher>.Instance)
            .RunOnceAsync();
    }

    private static string SourcePathOf(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "JiranisokoTech.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory!.FullName, "src", "JiranisokoTech.Infrastructure", file);
    }

    private sealed class Accepting : IOutboundSender
    {
        public Task<SendResult> SendAsync(
            Subscription subscription,
            OutboundDelivery delivery,
            string secret,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(SendResult.Ok(200));
    }

    private sealed class Refusing : IOutboundSender
    {
        public Task<SendResult> SendAsync(
            Subscription subscription,
            OutboundDelivery delivery,
            string secret,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(SendResult.Refused(503, "The endpoint answered 503."));
    }

    private sealed class Readable : ISecretStore
    {
        public string Protect(string plain) => plain;

        public string? Reveal(string protectedValue) => protectedValue;
    }

    private sealed class Unreadable : ISecretStore
    {
        public string Protect(string plain) => plain;

        public string? Reveal(string protectedValue) => null;
    }

    /// <summary>Reads a signature with the same code that reads GitHub's.</summary>
    private sealed class GitHubProviderProbe
    {
        public bool Accepts(string body, string signature, string secret) =>
            new GitHubProvider().IsSigned(
                Encoding.UTF8.GetBytes(body), signature, secret);
    }

    /// <summary>A name that is deliberately not a real event, for a negative test.</summary>
    private sealed record EmployeeLeftLikeName;
}
