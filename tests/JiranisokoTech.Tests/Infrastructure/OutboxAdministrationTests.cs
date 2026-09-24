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
/// Putting back the events that gave up.
/// </summary>
/// <remarks>
/// The outbox has kept abandoned rows since it was written, with a remark saying they stay
/// "so somebody can see it" — and until now there was nowhere to see them and nothing to
/// do about them. The machinery screen counted them and told the reader they "will not be
/// tried again without somebody", which was true in a way nobody intended: there was no
/// somebody.
///
/// What these tests are really about is the end of that sentence. A revive that leaves the
/// row where it was, or resets the wrong column, produces a screen that says it did
/// something — and the fault it was meant to fix is still there, now with a person
/// convinced it is not.
/// </remarks>
public class OutboxAdministrationTests
{
    /// <summary>
    /// The whole point, end to end: fix the handler, put it back, and it happens.
    /// </summary>
    /// <remarks>
    /// Asserted by running the dispatcher afterwards rather than by reading the columns,
    /// because the columns are not the claim. The claim is that a revived row is one the
    /// dispatcher will pick up — and the dispatcher's query has four conditions on it, any
    /// of which a plausible-looking revive could leave unsatisfied.
    /// </remarks>
    [Fact]
    public async Task An_event_put_back_after_the_handler_is_fixed_actually_happens()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        var settings = new OutboxOptions { MaxAttempts = 2, FirstRetryDelay = TimeSpan.FromSeconds(1) };
        var broken = new Thrower<GadgetMade>("the handler of the day");

        await RaiseAsync(db, "Payment gateway");

        for (var pass = 0; pass < settings.MaxAttempts; pass++)
        {
            await DispatchAsync(db, settings, broken);
            db.Clock.Advance(TimeSpan.FromHours(2));
        }

        Assert.NotNull((await SingleMessageAsync(db)).AbandonedAt);

        // The release that fixes it. Nothing retries an abandoned row on its own, so the
        // fixed handler sees nothing.
        var fixedHandler = new Recorder<GadgetMade>();

        Assert.Equal(0, await DispatchAsync(db, settings, fixedHandler));
        Assert.Empty(fixedHandler.Received);

        // Somebody presses the button.
        Assert.True(await ReviveAsync(db));

        Assert.Equal(1, await DispatchAsync(db, settings, fixedHandler));

        var handled = Assert.Single(fixedHandler.Received);
        Assert.Equal("Payment gateway", handled.Name);

        var message = await SingleMessageAsync(db);

        Assert.NotNull(message.DispatchedAt);
        Assert.Null(message.AbandonedAt);
        Assert.Null(message.Error);
    }

    /// <summary>
    /// The failed attempts are forgotten, because they were made against other code.
    /// </summary>
    /// <remarks>
    /// Without this the button is a lie for the case it exists for. A row abandoned at the
    /// attempt ceiling, revived with its count intact, is one failure away from being
    /// abandoned again — so the first transient hiccup after the fix undoes the fix, and
    /// the person who pressed the button has no reason to look again.
    ///
    /// This is also what the two sibling queues do, in <c>WebhookDelivery.Replay</c> and
    /// <c>OutboundDelivery.Retry</c>, with the same reasoning written next to them.
    /// </remarks>
    [Fact]
    public async Task Putting_an_event_back_forgets_the_attempts_made_against_the_old_code()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        var settings = new OutboxOptions { MaxAttempts = 2, FirstRetryDelay = TimeSpan.FromSeconds(1) };
        var broken = new Thrower<GadgetMade>("thoroughly broken");

        await RaiseAsync(db, "Payment gateway");

        for (var pass = 0; pass < settings.MaxAttempts; pass++)
        {
            await DispatchAsync(db, settings, broken);
            db.Clock.Advance(TimeSpan.FromHours(2));
        }

        /*
         * Asserted as "more than none" rather than as MaxAttempts, because the two are not
         * the same number and the difference is the dispatcher's business, not this test's.
         * Abandon does not spend an attempt — the attempt that would have been the last one
         * is never made — so a row abandoned at a ceiling of two carries one. Writing the
         * ceiling here would be asserting the dispatcher's arithmetic from the wrong file,
         * and it would break the next time somebody changed where the check sits.
         */
        var before = await SingleMessageAsync(db);

        Assert.True(
            before.Attempts > 0,
            "This test needs a row with failures behind it, or it proves nothing.");

        await ReviveAsync(db);

        var message = await SingleMessageAsync(db);

        Assert.Equal(0, message.Attempts);
        Assert.True(message.IsPending);

        // And it may be tried right away rather than waiting out the backoff it had.
        Assert.Equal(db.Clock.Now, message.NextAttemptAt);
    }

    /// <summary>
    /// A message that went through is not something to put back.
    /// </summary>
    /// <remarks>
    /// The guard matters because of what dispatched means: every handler ran. Reviving one
    /// would run them all a second time — a second receipt to a client, a second release of
    /// a leaver's work — for a change whose consequences already happened.
    /// </remarks>
    [Fact]
    public async Task An_event_that_already_went_through_cannot_be_put_back()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        await RaiseAsync(db, "Payment gateway");
        Assert.Equal(1, await DispatchAsync(db, new Recorder<GadgetMade>()));

        var dispatched = await SingleMessageAsync(db);

        Assert.NotNull(dispatched.DispatchedAt);
        Assert.False(await ReviveAsync(db, dispatched.Id));

        var after = await SingleMessageAsync(db);

        Assert.Equal(dispatched.DispatchedAt, after.DispatchedAt);
    }

    /// <summary>
    /// Everything at once, which is the shape this failure actually arrives in.
    /// </summary>
    [Fact]
    public async Task Putting_them_all_back_puts_back_every_one_that_gave_up()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        for (var made = 0; made < 3; made++)
        {
            await StoreAsync(db, Abandoned(db, $"Gadget {made}"));
        }

        // One that went through, to prove the sweep does not touch it.
        await RaiseAsync(db, "Already fine");
        await DispatchAsync(db, new Recorder<GadgetMade>());

        var administration = Administration(db);

        Assert.Equal(3, await administration.ReviveEverythingAbandonedAsync());

        await using var read = db.NewContext();

        Assert.Equal(0, await read.Outbox.CountAsync(one => one.AbandonedAt != null));
        Assert.Equal(3, await read.Outbox.CountAsync(one => one.DispatchedAt == null));
        Assert.Equal(1, await read.Outbox.CountAsync(one => one.DispatchedAt != null));
    }

    /// <summary>
    /// A row whose event class is gone is said to be, rather than left to be discovered.
    /// </summary>
    /// <remarks>
    /// The dispatcher abandons these within seconds of any revive, permanently, and there
    /// is nothing anybody can do about it — the payload names a type this build does not
    /// have. A screen that offered the same button with the same wording would have a
    /// person pressing it, watching the row reappear, and pressing it again.
    /// </remarks>
    [Fact]
    public async Task An_event_this_build_no_longer_has_says_so()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        await StoreAsync(db, Abandoned(db, "Still here"));

        var gone = new OutboxMessage("SomethingDeletedInVersionTwo", "{}", db.Clock.Now);
        gone.Abandon("No handler for it.", db.Clock.Now);
        await StoreAsync(db, gone);

        var rows = await Administration(db).AbandonedAsync();

        Assert.Equal(2, rows.Count);
        Assert.True(rows.Single(row => row.Type == nameof(GadgetMade)).StillInTheCode);
        Assert.False(rows.Single(row => row.Type != nameof(GadgetMade)).StillInTheCode);
    }

    /// <summary>
    /// The lists are capped; the counts are not, and the screen says so from the counts.
    /// </summary>
    /// <remarks>
    /// Section 77 found the same fault five times over — a screen reading every row in a
    /// table to show fifty of them. This queue is the worst candidate for it, because the
    /// only time anybody opens this screen is the time the queue is long.
    ///
    /// And the counts have to come from the database rather than from the list's length,
    /// or a list capped at a hundred is indistinguishable from a queue of exactly a
    /// hundred — and the screen would stop saying there was more.
    /// </remarks>
    [Fact]
    public async Task The_lists_are_capped_and_the_totals_are_counted_separately()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        var tooMany = OutboxAdministration.MostShown + 5;

        await using (var write = db.NewContext())
        {
            for (var made = 0; made < tooMany; made++)
            {
                var message = Abandoned(db, $"Gadget {made}");
                write.Outbox.Add(message);
            }

            await write.SaveChangesAsync();
        }

        var administration = Administration(db);

        Assert.Equal(OutboxAdministration.MostShown, (await administration.AbandonedAsync()).Count);
        Assert.Equal(tooMany, (await administration.CountsAsync()).Abandoned);
    }

    /// <summary>
    /// Oldest first, because these are work to redo rather than records to read.
    /// </summary>
    [Fact]
    public async Task The_oldest_event_is_first_in_the_list()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        await StoreAsync(db, Abandoned(db, "First"));
        db.Clock.Advance(TimeSpan.FromMinutes(5));
        await StoreAsync(db, Abandoned(db, "Second"));

        var rows = await Administration(db).AbandonedAsync();

        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].OccurredAt < rows[1].OccurredAt);
    }

    /// <summary>The class name is also offered as something a person can read.</summary>
    [Fact]
    public void An_event_name_is_offered_in_words()
    {
        var row = new OutboxRow(
            Guid.CreateVersion7(), "EmployeeLeftTheFirm", default, null, null, 0, null, true);

        Assert.Equal("Employee left the firm", row.Said);
    }

    private static OutboxAdministration Administration(DatabaseFixture db) =>
        new(
            db.NewContext(),
            // An explicit list rather than a scan of this assembly, which also holds the
            // deliberately clashing events in DomainEventRegistryTests.
            DomainEventRegistry.From([typeof(GadgetMade), typeof(GadgetRenamed)]),
            Options.Create(new OutboxOptions()),
            db.Clock,
            NullLogger<OutboxAdministration>.Instance);

    /// <summary>Put back the only abandoned row there is.</summary>
    private static async Task<bool> ReviveAsync(DatabaseFixture db)
    {
        await using var read = db.NewContext();
        var id = await read.Outbox
            .Where(one => one.AbandonedAt != null)
            .Select(one => one.Id)
            .SingleAsync();

        return await ReviveAsync(db, id);
    }

    private static async Task<bool> ReviveAsync(DatabaseFixture db, Guid id) =>
        await Administration(db).ReviveAsync(id);

    /// <summary>A row already in the state a handler that never worked leaves it in.</summary>
    /// <remarks>
    /// Built directly rather than by running a broken handler to the ceiling, because that
    /// takes a loop and a clock per row, and what is under test here is the reading and
    /// the reviving rather than how a row got abandoned — which
    /// <see cref="OutboxDispatcherTests"/> covers.
    /// </remarks>
    private static OutboxMessage Abandoned(DatabaseFixture db, string name)
    {
        var message = new OutboxMessage(nameof(GadgetMade), Made(name), db.Clock.Now);

        message.MarkFailed("Broken.", db.Clock.Now, TimeSpan.FromMinutes(1));
        message.Abandon("Broken, and out of attempts.", db.Clock.Now);

        return message;
    }

    private static async Task<Guid> RaiseAsync(DatabaseFixture db, string name)
    {
        await using var write = db.NewContext();

        var gadget = new Gadget(name);
        write.Gadgets.Add(gadget);
        await write.SaveChangesAsync();

        return gadget.Id;
    }

    /// <summary>A GadgetMade payload, written the way the context writes one.</summary>
    private static string Made(string name) =>
        $$"""{"gadgetId":"{{Guid.CreateVersion7()}}","name":"{{name}}"}""";

    private static async Task StoreAsync(DatabaseFixture db, OutboxMessage message)
    {
        await using var write = db.NewContext();

        write.Outbox.Add(message);
        await write.SaveChangesAsync();
    }

    private static async Task<OutboxMessage> SingleMessageAsync(DatabaseFixture db)
    {
        await using var read = db.NewContext();

        return await read.Outbox.OrderBy(message => message.OccurredAt).FirstAsync();
    }

    private static Task<int> DispatchAsync(DatabaseFixture db, params object[] handlers) =>
        DispatchAsync(db, new OutboxOptions(), handlers);

    private static async Task<int> DispatchAsync(
        DatabaseFixture db, OutboxOptions settings, params object[] handlers)
    {
        await using var context = db.NewContext();

        var services = new ServiceCollection();

        foreach (var handler in handlers)
        {
            foreach (var contract in handler.GetType().GetInterfaces()
                .Where(one => one.IsGenericType
                    && one.GetGenericTypeDefinition() == typeof(IDomainEventHandler<>)))
            {
                services.AddSingleton(contract, handler);
            }
        }

        var dispatcher = new OutboxDispatcher(
            context,
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
