using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Infrastructure.Messaging;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JiranisokoTech.Tests.Infrastructure;

/// <summary>
/// The dispatcher: what it delivers, and what it does when delivery fails.
/// </summary>
/// <remarks>
/// The happy path is the least interesting part. What decides whether this is
/// safe to run against a real mail server is the behaviour on failure — whether
/// a broken handler retries forever, whether a poisoned row blocks the ones
/// behind it, whether two instances can both send the same email.
/// </remarks>
public class OutboxDispatcherTests
{
    [Fact]
    public async Task An_event_reaches_its_handler_with_the_facts_intact()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        var handler = new Recorder<GadgetMade>();

        var gadget = await RaiseAsync(db, "Payment gateway");

        var settled = await RunAsync(db, handler);

        Assert.Equal(1, settled);

        var received = Assert.Single(handler.Received);
        Assert.Equal(gadget, received.GadgetId);
        Assert.Equal("Payment gateway", received.Name);

        var message = await SingleMessageAsync(db);

        Assert.False(message.IsPending);
        Assert.Equal(db.Clock.Now, message.DispatchedAt);
        Assert.Null(message.Error);
    }

    /// <summary>
    /// An event is a fact, and a fact nobody has subscribed to yet is still
    /// true. Leaving those pending would fill the table with rows waiting for
    /// code that may never be written.
    /// </summary>
    [Fact]
    public async Task An_event_nobody_handles_is_settled_rather_than_left_waiting()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        await RaiseAsync(db, "Unwatched");

        Assert.Equal(1, await RunAsync(db));

        Assert.False((await SingleMessageAsync(db)).IsPending);
    }

    [Fact]
    public async Task Every_handler_registered_for_an_event_gets_it()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        var first = new Recorder<GadgetMade>();
        var second = new Recorder<GadgetMade>();

        await RaiseAsync(db, "Watched twice");
        await RunAsync(db, first, second);

        Assert.Single(first.Received);
        Assert.Single(second.Received);
    }

    [Fact]
    public async Task A_handler_that_throws_leaves_the_message_to_try_again()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        await RaiseAsync(db, "Breaks things");
        await RunAsync(db, new Thrower<GadgetMade>("the mail server said no"));

        var message = await SingleMessageAsync(db);

        Assert.True(message.IsPending);
        Assert.Equal(1, message.Attempts);
        Assert.Contains("the mail server said no", message.Error);

        // And not immediately: without a wait, a broken handler is retried
        // several times a minute forever and its errors bury the log.
        Assert.NotNull(message.NextAttemptAt);
        Assert.True(message.NextAttemptAt > db.Clock.Now);
    }

    [Fact]
    public async Task A_message_waiting_out_its_backoff_is_left_alone()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        var handler = new Thrower<GadgetMade>("still broken");

        await RaiseAsync(db, "Breaks things");
        await RunAsync(db, handler);

        // A second pass in the same moment must not touch it.
        Assert.Equal(0, await RunAsync(db, handler));
        Assert.Equal(1, (await SingleMessageAsync(db)).Attempts);

        // Once the wait is over it is tried again.
        db.Clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(1, await RunAsync(db, handler));
        Assert.Equal(2, (await SingleMessageAsync(db)).Attempts);
    }

    /// <summary>
    /// Something has to stop. A handler broken by a code change would otherwise
    /// still be writing errors next week, and the row it is stuck on would be
    /// indistinguishable from one that is merely slow.
    /// </summary>
    [Fact]
    public async Task A_message_that_keeps_failing_is_eventually_given_up_on()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        var settings = new OutboxOptions { MaxAttempts = 3, FirstRetryDelay = TimeSpan.FromSeconds(1) };
        var handler = new Thrower<GadgetMade>("permanently broken");

        await RaiseAsync(db, "Hopeless");

        for (var pass = 0; pass < settings.MaxAttempts; pass++)
        {
            await RunAsync(db, settings, handler);
            db.Clock.Advance(TimeSpan.FromHours(2));
        }

        var message = await SingleMessageAsync(db);

        Assert.False(message.IsPending);
        Assert.NotNull(message.AbandonedAt);
        Assert.Contains("permanently broken", message.Error);

        // And it stays given up on: nothing picks it up again.
        Assert.Equal(0, await RunAsync(db, settings, handler));
        Assert.Equal(settings.MaxAttempts, handler.Attempts);
    }

    /// <summary>
    /// A release that deletes an event class leaves rows nothing can rebuild.
    /// Retrying those until the end of time only fills the log.
    /// </summary>
    [Fact]
    public async Task A_message_naming_an_event_this_build_lacks_is_given_up_on()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        await StoreAsync(db, new OutboxMessage("SomethingDeletedInVersionTwo", "{}", db.Clock.Now));

        Assert.Equal(1, await RunAsync(db));

        var message = await SingleMessageAsync(db);

        Assert.NotNull(message.AbandonedAt);
        Assert.Contains("SomethingDeletedInVersionTwo", message.Error);
    }

    [Fact]
    public async Task A_payload_that_cannot_be_read_is_given_up_on()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        await StoreAsync(db, new OutboxMessage(nameof(GadgetMade), "not json at all", db.Clock.Now));

        Assert.Equal(1, await RunAsync(db));

        Assert.NotNull((await SingleMessageAsync(db)).AbandonedAt);
    }

    /// <summary>
    /// One unreadable row must not stop the ones behind it. A queue that stalls
    /// on its first poisoned message is a queue that stops delivering entirely.
    /// </summary>
    [Fact]
    public async Task A_poisoned_message_does_not_block_the_ones_behind_it()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        var handler = new Recorder<GadgetMade>();

        await StoreAsync(db, new OutboxMessage(nameof(GadgetMade), "not json at all", db.Clock.Now));
        await RaiseAsync(db, "Perfectly fine");

        Assert.Equal(2, await RunAsync(db, handler));

        Assert.Single(handler.Received);
        Assert.Equal("Perfectly fine", handler.Received[0].Name);
    }

    /// <summary>
    /// Two instances of the application both poll this table. Without a claim
    /// they would both publish the same row: two rejection emails to one
    /// candidate, from a system whose logs both look correct.
    /// </summary>
    [Fact]
    public async Task A_message_another_dispatcher_holds_is_left_to_it()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        var handler = new Recorder<GadgetMade>();

        await RaiseAsync(db, "Contested");
        await ClaimElsewhereAsync(db, db.Clock.Now);

        Assert.Equal(0, await RunAsync(db, handler));
        Assert.Empty(handler.Received);
    }

    /// <summary>
    /// But a claim held by a process that died must not strand the message for
    /// good, so it expires.
    /// </summary>
    [Fact]
    public async Task A_claim_left_behind_by_a_dead_process_expires()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        var handler = new Recorder<GadgetMade>();
        var settings = new OutboxOptions { ClaimTimeout = TimeSpan.FromMinutes(5) };

        await RaiseAsync(db, "Stranded");
        await ClaimElsewhereAsync(db, db.Clock.Now);

        db.Clock.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(1, await RunAsync(db, settings, handler));
        Assert.Single(handler.Received);
    }

    /// <summary>
    /// Delivering a backlog newest-first would be a strange thing to explain.
    /// </summary>
    /// <remarks>
    /// The rows are written here with times set by hand rather than by raising
    /// two events. An event stamps itself from the wall clock, so two raised in
    /// quick succession can share a millisecond, and a test that ordered those
    /// would pass or fail by luck.
    /// </remarks>
    [Fact]
    public async Task The_oldest_message_is_taken_first()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        var handler = new Recorder<GadgetMade>();

        var earlier = db.Clock.Now - TimeSpan.FromHours(1);

        await StoreAsync(db, new OutboxMessage(nameof(GadgetMade), Made("Newer"), db.Clock.Now));
        await StoreAsync(db, new OutboxMessage(nameof(GadgetMade), Made("Older"), earlier));

        // One at a time, so the order is the dispatcher choosing and not an
        // accident of how a batch was enumerated.
        await RunAsync(db, new OutboxOptions { BatchSize = 1 }, handler);

        Assert.Equal("Older", Assert.Single(handler.Received).Name);
    }

    // --- sweeping --------------------------------------------------------

    /// <summary>
    /// Every change in the system writes a row here and nothing ever reads a
    /// delivered one again. Without a sweep this becomes the largest table in
    /// the database, made entirely of things that already happened.
    /// </summary>
    [Fact]
    public async Task Delivered_messages_are_swept_once_they_are_old_enough()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        await RaiseAsync(db, "Long ago");
        await RunAsync(db);

        Assert.Equal(0, await PruneAsync(db));

        db.Clock.Advance(TimeSpan.FromDays(31));

        Assert.Equal(1, await PruneAsync(db));
        Assert.Equal(0, await CountAsync(db));
    }

    /// <summary>
    /// Abandoned messages are kept. They are the ones somebody has to look at,
    /// and deleting the record of a failure on a timer is how a system loses
    /// the only evidence that it went wrong.
    /// </summary>
    [Fact]
    public async Task An_abandoned_message_is_never_swept()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        await StoreAsync(db, new OutboxMessage("GoneInVersionTwo", "{}", db.Clock.Now));
        await RunAsync(db);

        db.Clock.Advance(TimeSpan.FromDays(365));

        Assert.Equal(0, await PruneAsync(db));
        Assert.Equal(1, await CountAsync(db));
    }

    [Fact]
    public async Task A_message_still_waiting_is_never_swept()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        await RaiseAsync(db, "Not delivered yet");

        db.Clock.Advance(TimeSpan.FromDays(365));

        Assert.Equal(0, await PruneAsync(db));
        Assert.True((await SingleMessageAsync(db)).IsPending);
    }

    // --- the plumbing ------------------------------------------------------

    private static async Task<Guid> RaiseAsync(DatabaseFixture db, string name)
    {
        await using var write = db.NewContext();

        var gadget = new Gadget(name);
        write.Gadgets.Add(gadget);
        await write.SaveChangesAsync();

        return gadget.Id;
    }

    /// <summary>
    /// A GadgetMade payload, written the way the context writes one.
    /// </summary>
    /// <remarks>
    /// By hand because the serializer settings the outbox uses are internal to
    /// the infrastructure assembly, and opening them up so a test could reach
    /// them would be widening the production surface to suit a test.
    /// </remarks>
    private static string Made(string name) =>
        $$"""{"gadgetId":"{{Guid.CreateVersion7()}}","name":"{{name}}"}""";

    private static async Task StoreAsync(DatabaseFixture db, OutboxMessage message)
    {
        await using var write = db.NewContext();

        write.Outbox.Add(message);
        await write.SaveChangesAsync();
    }

    /// <summary>Put a live claim on every pending row, as a second instance would.</summary>
    private static async Task ClaimElsewhereAsync(DatabaseFixture db, DateTimeOffset at)
    {
        await using var write = db.NewContext();

        await write.Outbox
            .Where(message => message.DispatchedAt == null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(message => message.ClaimedBy, Guid.CreateVersion7())
                .SetProperty(message => message.ClaimedAt, at));
    }

    private static async Task<OutboxMessage> SingleMessageAsync(DatabaseFixture db)
    {
        await using var read = db.NewContext();

        return await read.Outbox.OrderBy(message => message.OccurredAt).FirstAsync();
    }

    private static async Task<int> CountAsync(DatabaseFixture db)
    {
        await using var read = db.NewContext();

        return await read.Outbox.CountAsync();
    }

    private static async Task<int> PruneAsync(DatabaseFixture db)
    {
        await using var context = db.NewContext();

        var dispatcher = new OutboxDispatcher(
            context,
            DomainEventRegistry.From([typeof(GadgetMade)]),
            new ServiceCollection().BuildServiceProvider(),
            db.Clock,
            Options.Create(new OutboxOptions()),
            NullLogger<OutboxDispatcher>.Instance);

        return await dispatcher.PruneAsync();
    }

    private static Task<int> RunAsync(DatabaseFixture db, params object[] handlers) =>
        RunAsync(db, new OutboxOptions(), handlers);

    private static async Task<int> RunAsync(
        DatabaseFixture db, OutboxOptions settings, params object[] handlers)
    {
        await using var context = db.NewContext();

        var services = new ServiceCollection();

        foreach (var handler in handlers)
        {
            // Registered against every IDomainEventHandler<T> it implements, so
            // a test double looks to the dispatcher exactly like a real handler.
            foreach (var contract in handler.GetType().GetInterfaces()
                .Where(i => i.IsGenericType
                    && i.GetGenericTypeDefinition() == typeof(IDomainEventHandler<>)))
            {
                services.AddSingleton(contract, handler);
            }
        }

        var dispatcher = new OutboxDispatcher(
            context,
            // An explicit list rather than a scan of this assembly, which also
            // holds the deliberately clashing events in DomainEventRegistryTests.
            DomainEventRegistry.From([typeof(GadgetMade), typeof(GadgetRenamed)]),
            services.BuildServiceProvider(),
            db.Clock,
            Options.Create(settings),
            NullLogger<OutboxDispatcher>.Instance);

        return await dispatcher.RunOnceAsync();
    }

    private sealed class Recorder<TEvent> : IDomainEventHandler<TEvent>
        where TEvent : IDomainEvent
    {
        public List<TEvent> Received { get; } = [];

        public Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken = default)
        {
            Received.Add(domainEvent);

            return Task.CompletedTask;
        }
    }

    private sealed class Thrower<TEvent>(string because) : IDomainEventHandler<TEvent>
        where TEvent : IDomainEvent
    {
        public int Attempts { get; private set; }

        public Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken = default)
        {
            Attempts++;

            throw new InvalidOperationException(because);
        }
    }
}
