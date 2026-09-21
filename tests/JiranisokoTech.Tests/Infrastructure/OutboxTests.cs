using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Tests.Infrastructure;

/// <summary>
/// The transactional outbox.
///
/// The gap it closes is easy to miss: save the change, then publish the event,
/// and a process that dies between the two has committed a hire nobody was told
/// about. Writing the event in the same transaction means either both land or
/// neither does.
/// </summary>
public class OutboxTests
{
    [Fact]
    public async Task An_event_raised_by_an_entity_is_written_with_the_change()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        await using (var write = db.NewContext())
        {
            write.Gadgets.Add(new Gadget("Payment gateway"));
            await write.SaveChangesAsync();
        }

        await using var read = db.NewContext();
        var message = await read.Outbox.SingleAsync();

        Assert.Equal(nameof(GadgetMade), message.Type);
        Assert.True(message.IsPending);
        Assert.Null(message.DispatchedAt);
        Assert.Equal(0, message.Attempts);
    }

    /// <summary>
    /// Events carry ids and values, never entities — they are queued, and a
    /// loaded object graph that crosses that boundary arrives stale or not at all.
    /// </summary>
    [Fact]
    public async Task The_payload_carries_the_facts_needed_to_handle_it()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        Guid id;

        await using (var write = db.NewContext())
        {
            var gadget = new Gadget("Payment gateway");
            write.Gadgets.Add(gadget);
            await write.SaveChangesAsync();
            id = gadget.Id;
        }

        await using var read = db.NewContext();
        var message = await read.Outbox.SingleAsync();

        using var payload = JsonDocument.Parse(message.Payload);

        Assert.Equal(id, payload.RootElement.GetProperty("gadgetId").GetGuid());
        Assert.Equal("Payment gateway", payload.RootElement.GetProperty("name").GetString());
    }

    /// <summary>
    /// The change and its event commit together. Neither can exist alone.
    /// </summary>
    [Fact]
    public async Task A_failed_save_writes_neither_the_change_nor_the_event()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        await using (var write = db.NewContext())
        {
            // Null in a NOT NULL column. The first version of this test used an
            // over-long string, which SQLite accepts without complaint — column
            // lengths are advisory there — so the test passed for the wrong
            // reason and proved nothing about rollback.
            write.Gadgets.Add(new Gadget(null!));

            await Assert.ThrowsAnyAsync<Exception>(() => write.SaveChangesAsync());
        }

        await using var read = db.NewContext();

        Assert.Equal(0, await read.Gadgets.CountAsync());
        Assert.Equal(0, await read.Outbox.CountAsync());
        Assert.Equal(0, await read.AuditEntries.CountAsync());
    }

    /// <summary>
    /// An entity tracked across two saves must not republish its history.
    /// </summary>
    [Fact]
    public async Task Events_are_written_once_even_when_the_entity_is_saved_again()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        await using var write = db.NewContext();

        var gadget = new Gadget("First");
        write.Gadgets.Add(gadget);
        await write.SaveChangesAsync();

        gadget.Rename("Second");
        await write.SaveChangesAsync();

        await using var read = db.NewContext();
        var messages = await read.Outbox.OrderBy(m => m.OccurredAt).ToListAsync();

        Assert.Equal([nameof(GadgetMade), nameof(GadgetRenamed)], messages.Select(m => m.Type));
    }

    [Fact]
    public async Task An_entity_that_raises_nothing_writes_nothing()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        await using (var write = db.NewContext())
        {
            write.Trinkets.Add(new Trinket("Quiet"));
            await write.SaveChangesAsync();
        }

        await using var read = db.NewContext();

        Assert.Equal(0, await read.Outbox.CountAsync());
    }

    [Fact]
    public async Task A_dispatched_message_records_when_and_clears_its_error()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        await using (var write = db.NewContext())
        {
            write.Gadgets.Add(new Gadget("Thing"));
            await write.SaveChangesAsync();
        }

        await using (var dispatch = db.NewContext())
        {
            var message = await dispatch.Outbox.SingleAsync();
            message.MarkFailed("Handler threw");
            message.MarkDispatched(db.Clock.Now);
            await dispatch.SaveChangesAsync();
        }

        await using var read = db.NewContext();
        var settled = await read.Outbox.SingleAsync();

        Assert.False(settled.IsPending);
        Assert.Equal(db.Clock.Now, settled.DispatchedAt);
        Assert.Equal(1, settled.Attempts);
        Assert.Null(settled.Error);
    }

    /// <summary>
    /// A handler's stack trace can run to kilobytes, and a hundred of them would
    /// make the outbox the largest table in the database.
    /// </summary>
    [Fact]
    public async Task A_long_failure_is_truncated_rather_than_stored_whole()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        await using (var write = db.NewContext())
        {
            write.Gadgets.Add(new Gadget("Thing"));
            await write.SaveChangesAsync();
        }

        await using (var fail = db.NewContext())
        {
            var failing = await fail.Outbox.SingleAsync();
            failing.MarkFailed(new string('e', 5000));
            await fail.SaveChangesAsync();
        }

        await using var read = db.NewContext();
        var stored = await read.Outbox.SingleAsync();

        Assert.Equal(2000, stored.Error!.Length);
        Assert.True(stored.IsPending);
    }
}
