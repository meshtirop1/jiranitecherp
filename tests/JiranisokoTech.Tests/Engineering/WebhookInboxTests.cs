using System.Security.Cryptography;
using System.Text;
using JiranisokoTech.Application.Engineering;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.Engineering;
using JiranisokoTech.Infrastructure.Work;
using JiranisokoTech.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace JiranisokoTech.Tests.Engineering;

/// <summary>
/// A delivery arriving, and what it costs to get it wrong.
/// </summary>
/// <remarks>
/// These run against a real database because every guarantee being asserted is a
/// database guarantee. Idempotency is a unique index; the ordering the dispatcher
/// relies on is an ORDER BY; the record that survives a handler throwing is a row
/// committed before the handler ran. None of that can be shown with a fake.
/// </remarks>
public class WebhookInboxTests
{
    private const string Secret = "the-secret-github-also-holds";

    /// <summary>
    /// A push becomes commits, linked to the work its branch named.
    /// </summary>
    /// <remarks>
    /// The whole integration in one test. Nobody typed anything into this
    /// system: a developer named their branch after the work, pushed, and the
    /// record of what was done appeared against the right task.
    /// </remarks>
    [Fact]
    public async Task A_signed_push_is_recorded_against_the_work_its_branch_named()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var work = await GiveWork(fixture, number: 412);
        await Connect(fixture);

        var receipt = await Receive(fixture, "push", Push("feature/412-payment-api"));

        Assert.Equal(Reception.Accepted, receipt.Outcome);

        await Dispatch(fixture);

        await using var context = fixture.NewContext();
        var commit = await context.Commits.SingleAsync();

        Assert.Equal("9a1c0ff4e6b3d2a18f7c5e0b4d3a2916f8e7c0d5", commit.Sha);
        Assert.Equal(work, commit.WorkItemId);
        Assert.Equal("feature/412-payment-api", commit.Branch);

        var delivery = await context.Deliveries.SingleAsync();
        Assert.Equal(DeliveryStatus.Handled, delivery.Status);
    }

    /// <summary>
    /// An unsigned request records nothing at all.
    /// </summary>
    /// <remarks>
    /// Not merely refused: it must leave no trace in the tables a stranger could
    /// otherwise fill. An endpoint that recorded every probe would let anybody
    /// who found the address grow the database at will.
    /// </remarks>
    [Fact]
    public async Task An_unsigned_request_is_refused_and_recorded_nowhere()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await Connect(fixture);

        var receipt = await Receive(
            fixture, "push", Push("feature/412-payment-api"), signature: "sha256=" + new string('a', 64));

        Assert.Equal(Reception.Unsigned, receipt.Outcome);

        await using var context = fixture.NewContext();
        Assert.Empty(await context.Deliveries.ToListAsync());
    }

    /// <summary>
    /// The same delivery twice is one delivery.
    /// </summary>
    /// <remarks>
    /// Providers retry, and this is also the replay protection: a captured body
    /// posted back later carries the same delivery identifier and is refused on
    /// exactly this check. The assertion that there is one commit rather than two
    /// is the part that would matter to somebody reading a task.
    /// </remarks>
    [Fact]
    public async Task A_redelivered_push_is_recorded_once()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await GiveWork(fixture, number: 412);
        await Connect(fixture);

        var body = Push("feature/412-payment-api");

        var first = await Receive(fixture, "push", body, deliveryId: "d-1");
        var second = await Receive(fixture, "push", body, deliveryId: "d-1");

        Assert.Equal(Reception.Accepted, first.Outcome);
        Assert.Equal(Reception.Duplicate, second.Outcome);

        await Dispatch(fixture);

        await using var context = fixture.NewContext();
        Assert.Single(await context.Deliveries.ToListAsync());
        Assert.Single(await context.Commits.ToListAsync());
    }

    /// <summary>
    /// The same commit arriving on a second push is not recorded twice.
    /// </summary>
    /// <remarks>
    /// A different guarantee from the one above, and needed as well as it. Two
    /// genuine deliveries — a push to a branch and a push of the same commit
    /// elsewhere — both pass the idempotency check, because they really are two
    /// deliveries. What stops the history doubling is the commit hash.
    /// </remarks>
    [Fact]
    public async Task A_commit_arriving_on_two_pushes_is_recorded_once()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await GiveWork(fixture, number: 412);
        await Connect(fixture);

        await Receive(fixture, "push", Push("feature/412-payment-api"), deliveryId: "d-1");
        await Receive(fixture, "push", Push("main"), deliveryId: "d-2");
        await Dispatch(fixture);

        await using var context = fixture.NewContext();

        Assert.Equal(2, await context.Deliveries.CountAsync());
        Assert.Single(await context.Commits.ToListAsync());
    }

    /// <summary>
    /// A delivery for a repository nobody connected is kept, and ignored.
    /// </summary>
    /// <remarks>
    /// The evidence that somebody pointed a webhook at this system and nothing
    /// was listening. Dropping it would make that indistinguishable from a
    /// webhook nobody ever configured, which is the harder thing to diagnose.
    /// </remarks>
    [Fact]
    public async Task A_delivery_for_an_unknown_repository_is_kept_and_ignored()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();

        var receipt = await Receive(fixture, "push", Push("feature/412-payment-api"));

        Assert.Equal(Reception.Accepted, receipt.Outcome);

        await using var context = fixture.NewContext();
        var delivery = await context.Deliveries.SingleAsync();

        Assert.Equal(DeliveryStatus.Ignored, delivery.Status);
        Assert.Null(delivery.RepositoryId);
        Assert.Empty(await context.Commits.ToListAsync());
    }

    /// <summary>
    /// A repository is matched however the payload spells it.
    /// </summary>
    /// <remarks>
    /// GitHub does not preserve the case somebody typed here, and a miss would
    /// file every delivery as belonging to nothing while the screen went on
    /// showing the repository as connected.
    /// </remarks>
    [Fact]
    public async Task A_repository_is_matched_without_regard_to_case()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await Connect(fixture, owner: "JiranisokoTech", name: "ERP");

        await Receive(fixture, "ping", """
            {"zen":"Anything added dilutes everything else.",
             "repository":{"full_name":"jiranisokotech/erp"}}
            """);

        await using var context = fixture.NewContext();
        Assert.NotNull((await context.Deliveries.SingleAsync()).RepositoryId);
    }

    /// <summary>
    /// A merge moves the work to review, and no further.
    /// </summary>
    /// <remarks>
    /// The promise the brief opens with: the engineer merged, and did not then
    /// have to tell the board. And the limit on it — accepting the work is still
    /// somebody's decision, so this stops at review rather than marking it done.
    /// </remarks>
    [Fact]
    public async Task A_merged_pull_request_puts_its_work_up_for_review()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var workId = await GiveWork(fixture, number: 412, started: true);
        await Connect(fixture);

        await Receive(fixture, "pull_request", PullRequest("closed", merged: true));
        await Dispatch(fixture);

        await using var context = fixture.NewContext();
        var pullRequest = await context.PullRequests.SingleAsync();

        Assert.Equal(PullRequestState.Merged, pullRequest.State);
        Assert.Equal(workId, pullRequest.WorkItemId);

        // The handler runs from the outbox, so it is invoked here directly —
        // what is being asserted is the rule, not the plumbing that carries it.
        await new SubmitWorkWhenPullRequestMerges(
                new WorkRepository(context),
                fixture.Clock,
                NullLogger<SubmitWorkWhenPullRequestMerges>.Instance)
            .HandleAsync(new PullRequestMerged(
                pullRequest.Id, pullRequest.RepositoryId, 412, pullRequest.Branch, workId,
                fixture.Clock.Now));

        await using var after = fixture.NewContext();
        Assert.Equal(
            WorkItemStatus.InReview,
            (await after.WorkItems.SingleAsync(item => item.Id == workId)).Status);
    }

    /// <summary>
    /// A stale close arriving after a merge does not un-merge it.
    /// </summary>
    /// <remarks>
    /// Providers do not promise order, and they redeliver after outages. Without
    /// this the board would show shipped work as abandoned because two
    /// deliveries arrived in the order the network chose.
    /// </remarks>
    [Fact]
    public async Task A_close_arriving_after_a_merge_is_ignored()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await GiveWork(fixture, number: 412);
        await Connect(fixture);

        await Receive(
            fixture, "pull_request", PullRequest("closed", merged: true), deliveryId: "d-1");
        await Receive(
            fixture, "pull_request", PullRequest("closed", merged: false), deliveryId: "d-2");
        await Dispatch(fixture);

        await using var context = fixture.NewContext();
        Assert.Equal(
            PullRequestState.Merged, (await context.PullRequests.SingleAsync()).State);
    }

    /// <summary>
    /// An approval delivered twice is one approval.
    /// </summary>
    [Fact]
    public async Task A_redelivered_review_is_recorded_once()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await GiveWork(fixture, number: 412);
        await Connect(fixture);

        await Receive(fixture, "pull_request", PullRequest("opened", merged: false), "d-1");
        await Receive(fixture, "pull_request_review", Review("approved"), "d-2");

        // A different delivery carrying the same review: a genuine second
        // delivery, so the inbox accepts it, and the review's own identifier is
        // what stops it counting twice.
        await Receive(fixture, "pull_request_review", Review("approved"), "d-3");
        await Dispatch(fixture);

        await using var context = fixture.NewContext();
        var pullRequest = await context.PullRequests.SingleAsync();

        Assert.Single(pullRequest.Reviews);
        Assert.True(pullRequest.IsApproved);
    }

    /// <summary>
    /// A review arriving before its pull request is retried, not lost.
    /// </summary>
    /// <remarks>
    /// Out-of-order delivery is normal. Swallowing this would silently drop the
    /// approval a release gate is waiting for, and nobody would know why the
    /// gate would not open.
    /// </remarks>
    [Fact]
    public async Task A_review_before_its_pull_request_waits_and_then_lands()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await GiveWork(fixture, number: 412);
        await Connect(fixture);

        await Receive(fixture, "pull_request_review", Review("approved"), "d-1");
        await Dispatch(fixture);

        await using (var context = fixture.NewContext())
        {
            var delivery = await context.Deliveries.SingleAsync();

            Assert.Equal(DeliveryStatus.Failed, delivery.Status);
            Assert.Equal(1, delivery.Attempts);
        }

        /*
         * The clock moves, so that the review is genuinely the older of the two
         * and is therefore tried first. Without this the two deliveries share a
         * timestamp, the order between them is whatever the database feels like,
         * and the test passes or fails on that rather than on the behaviour —
         * which is exactly how it first passed while asserting the wrong thing.
         */
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));

        await Receive(fixture, "pull_request", PullRequest("opened", merged: false), "d-2");
        await Dispatch(fixture);

        /*
         * The review fails a second time in that pass, because it is handled
         * before the pull request that would have satisfied it. This is the
         * ordinary cost of out-of-order delivery and it is worth asserting
         * rather than glossing: nothing is lost, the attempt is spent, and the
         * next pass lands it.
         */
        await using (var context = fixture.NewContext())
        {
            var review = await context.Deliveries
                .SingleAsync(one => one.Event == "pull_request_review");

            Assert.Equal(DeliveryStatus.Failed, review.Status);
            Assert.Equal(2, review.Attempts);
            Assert.Equal(1, await context.PullRequests.CountAsync());
        }

        await Dispatch(fixture);

        await using var after = fixture.NewContext();

        Assert.Single((await after.PullRequests.SingleAsync()).Reviews);
        Assert.Equal(
            DeliveryStatus.Handled,
            (await after.Deliveries
                .SingleAsync(one => one.Event == "pull_request_review")).Status);
    }

    /// <summary>
    /// A delivery that keeps failing stops being retried and waits for a person.
    /// </summary>
    /// <remarks>
    /// The alternative is a queue that retries forever, which is a queue nobody
    /// looks at — and the body is kept, so whoever fixes the cause can replay it
    /// rather than losing the week it was broken for.
    /// </remarks>
    [Fact]
    public async Task A_delivery_that_keeps_failing_is_eventually_dead_lettered()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await Connect(fixture);

        await Receive(fixture, "pull_request_review", Review("approved"), "d-1");

        for (var attempt = 0; attempt < WebhookDelivery.MaximumAttempts; attempt++)
        {
            await Dispatch(fixture);
        }

        await using var context = fixture.NewContext();
        var delivery = await context.Deliveries.SingleAsync();

        Assert.Equal(DeliveryStatus.DeadLettered, delivery.Status);
        Assert.Equal(WebhookDelivery.MaximumAttempts, delivery.Attempts);
        Assert.NotNull(delivery.Error);

        // The body survived, which is the only reason a replay is possible.
        Assert.Contains("approved", delivery.Payload);
    }

    /// <summary>
    /// A replay puts a dead-lettered delivery back, and it can then succeed.
    /// </summary>
    [Fact]
    public async Task A_replayed_delivery_is_handled_once_the_cause_is_gone()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await GiveWork(fixture, number: 412);
        await Connect(fixture);

        await Receive(fixture, "pull_request_review", Review("approved"), "d-1");

        for (var attempt = 0; attempt < WebhookDelivery.MaximumAttempts; attempt++)
        {
            await Dispatch(fixture);
        }

        // The cause was the missing pull request. It arrives.
        await Receive(fixture, "pull_request", PullRequest("opened", merged: false), "d-2");
        await Dispatch(fixture);

        Guid deadLettered;

        await using (var context = fixture.NewContext())
        {
            deadLettered = (await context.Deliveries
                .SingleAsync(one => one.Status == DeliveryStatus.DeadLettered)).Id;
        }

        await using (var context = fixture.NewContext())
        {
            await new EngineeringService(
                    new EngineeringRepository(context),
                    new WorkRepository(context),
                    new Secrets(),
                    fixture.Clock)
                .ReplayAsync(deadLettered);
        }

        await Dispatch(fixture);

        await using var after = fixture.NewContext();

        Assert.Equal(
            DeliveryStatus.Handled,
            (await after.Deliveries.SingleAsync(one => one.Id == deadLettered)).Status);
        Assert.Single((await after.PullRequests.SingleAsync()).Reviews);
    }

    /// <summary>
    /// With no secret configured, nothing is accepted.
    /// </summary>
    /// <remarks>
    /// The failure mode this prevents is the worst one available: a missing
    /// secret making every signature check pass vacuously, so the endpoint
    /// accepts whatever anybody posts. Refusing loudly is the only safe reading
    /// of "not configured".
    /// </remarks>
    [Fact]
    public async Task With_no_secret_configured_nothing_is_accepted()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();

        var inbox = new WebhookInbox(
            new EngineeringRepository(context),
            [new GitHubProvider()],
            new Secrets(configured: null),
            fixture.Clock,
            NullLogger<WebhookInbox>.Instance);

        var body = Encoding.UTF8.GetBytes(Push("feature/412-payment-api"));

        var receipt = await inbox.ReceiveAsync(
            GitProvider.GitHub, Headers(body, "push", "d-1", Secret), body);

        Assert.Equal(Reception.NotConfigured, receipt.Outcome);
        Assert.Empty(await context.Deliveries.ToListAsync());
    }

    /// <summary>A signed delivery missing its identifier is a bad request.</summary>
    [Fact]
    public async Task A_delivery_with_no_identifier_is_refused()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();

        var inbox = new WebhookInbox(
            new EngineeringRepository(context),
            [new GitHubProvider()],
            new Secrets(),
            fixture.Clock,
            NullLogger<WebhookInbox>.Instance);

        var body = Encoding.UTF8.GetBytes(Push("feature/412-payment-api"));
        var headers = Headers(body, "push", "d-1", Secret);
        headers.Remove("X-GitHub-Delivery");

        Assert.Equal(
            Reception.Malformed,
            (await inbox.ReceiveAsync(GitProvider.GitHub, headers, body)).Outcome);
    }

    /// <summary>
    /// Receiving a delivery notes that the provider is still talking to us.
    /// </summary>
    /// <remarks>
    /// The most useful field on the repositories screen. "Connected" and "last
    /// heard from three weeks ago" are different states, and only the second one
    /// tells anybody to go and look at the webhook configuration.
    /// </remarks>
    [Fact]
    public async Task Receiving_a_delivery_records_that_the_provider_was_heard_from()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await Connect(fixture);

        await Receive(fixture, "ping", """
            {"zen":"Speak like a human.","repository":{"full_name":"jiranisokotech/erp"}}
            """);

        await using var context = fixture.NewContext();
        Assert.Equal(
            fixture.Clock.Now,
            (await context.Repositories.SingleAsync()).LastDeliveryAt);
    }

    // --- the scaffolding ----------------------------------------------------

    private static async Task<Guid> GiveWork(
        DatabaseFixture fixture, int number, bool started = false)
    {
        await using var context = fixture.NewContext();

        var item = WorkItem.Raise(number, "Payment API retries", Guid.CreateVersion7());

        if (started)
        {
            item.MoveTo(WorkItemStatus.InProgress, fixture.Clock.Now);
        }

        context.WorkItems.Add(item);
        await context.SaveChangesAsync();

        return item.Id;
    }

    private static async Task Connect(
        DatabaseFixture fixture, string owner = "jiranisokotech", string name = "erp")
    {
        await using var context = fixture.NewContext();

        await new EngineeringService(
                new EngineeringRepository(context),
                new WorkRepository(context),
                new Secrets(),
                fixture.Clock)
            .ConnectAsync(GitProvider.GitHub, owner, name, Secret);
    }

    private static async Task<Receipt> Receive(
        DatabaseFixture fixture,
        string eventName,
        string payload,
        string deliveryId = "d-1",
        string? signature = null)
    {
        await using var context = fixture.NewContext();

        var inbox = new WebhookInbox(
            new EngineeringRepository(context),
            [new GitHubProvider()],
            new Secrets(),
            fixture.Clock,
            NullLogger<WebhookInbox>.Instance);

        var body = Encoding.UTF8.GetBytes(payload);
        var headers = Headers(body, eventName, deliveryId, Secret);

        if (signature is not null)
        {
            headers["X-Hub-Signature-256"] = signature;
        }

        return await inbox.ReceiveAsync(GitProvider.GitHub, headers, body);
    }

    private static async Task Dispatch(DatabaseFixture fixture)
    {
        await using var context = fixture.NewContext();

        await new DeliveryDispatcher(
                new EngineeringRepository(context),
                new WorkRepository(context),
                [new GitHubProvider()],
                fixture.Clock,
                NullLogger<DeliveryDispatcher>.Instance)
            .RunOnceAsync();
    }

    private static Dictionary<string, string> Headers(
        byte[] body, string eventName, string deliveryId, string secret) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["X-GitHub-Event"] = eventName,
            ["X-GitHub-Delivery"] = deliveryId,
            ["X-Hub-Signature-256"] = "sha256=" + Convert.ToHexStringLower(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)),
        };

    private static string Push(string branch) => $$"""
        {
          "ref": "refs/heads/{{branch}}",
          "repository": { "full_name": "jiranisokotech/erp" },
          "commits": [
            {
              "id": "9a1c0ff4e6b3d2a18f7c5e0b4d3a2916f8e7c0d5",
              "message": "Add the retry",
              "timestamp": "2026-09-22T14:03:11+03:00",
              "author": { "name": "Meshack Tirop", "username": "meshtirop1" }
            }
          ]
        }
        """;

    private static string PullRequest(string action, bool merged) => $$"""
        {
          "action": "{{action}}",
          "repository": { "full_name": "jiranisokotech/erp" },
          "pull_request": {
            "number": 412,
            "title": "Payment API retries",
            "merged": {{(merged ? "true" : "false")}},
            "created_at": "2026-09-22T14:00:00Z",
            "closed_at": "2026-09-22T16:45:00Z",
            "merged_at": {{(merged ? "\"2026-09-22T16:45:00Z\"" : "null")}},
            "head": { "ref": "feature/412-payment-api" },
            "user": { "login": "meshtirop1" }
          }
        }
        """;

    private static string Review(string state) => $$"""
        {
          "action": "submitted",
          "repository": { "full_name": "jiranisokotech/erp" },
          "review": { "id": 2748193, "state": "{{state}}", "user": { "login": "vincent" } },
          "pull_request": { "number": 412 }
        }
        """;

    /// <summary>The configured secret, as a stub.</summary>
    private sealed class Secrets(string? configured = Secret) : IWebhookSecrets
    {
        public string? For(GitProvider provider) => configured;
    }
}
