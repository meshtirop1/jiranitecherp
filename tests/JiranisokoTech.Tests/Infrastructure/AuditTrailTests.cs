using JiranisokoTech.Domain.Audit;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Tests.Infrastructure;

/// <summary>
/// What the trail records, what it refuses, and what it deliberately leaves out.
/// </summary>
public class AuditTrailTests
{
    private static readonly Guid Actor = Guid.CreateVersion7();

    [Fact]
    public async Task Creating_a_record_is_recorded_with_who_did_it()
    {
        await using var db = await DatabaseFixture.CreateAsync(Actor, "Charity Jepchirchir");

        await using (var write = db.NewContext())
        {
            write.Gadgets.Add(new Gadget("Payment gateway"));
            await write.SaveChangesAsync();
        }

        await using var read = db.NewContext();
        var entry = await read.AuditEntries.SingleAsync();

        Assert.Equal("gadget.added", entry.Action);
        Assert.Equal("Gadget", entry.SubjectType);
        Assert.Equal(Actor, entry.ActorId);
        Assert.Equal("Charity Jepchirchir", entry.ActorName);
        Assert.Equal(db.Clock.Now, entry.OccurredAt);

        Assert.Null(entry.Before);
        Assert.Equal("Payment gateway", entry.After!["Name"]);
    }

    [Fact]
    public async Task A_change_records_only_what_moved()
    {
        await using var db = await DatabaseFixture.CreateAsync(Actor, "Charity");

        Guid id;

        await using (var write = db.NewContext())
        {
            var gadget = new Gadget("Old name");
            write.Gadgets.Add(gadget);
            await write.SaveChangesAsync();
            id = gadget.Id;
        }

        await using (var write = db.NewContext())
        {
            var gadget = await write.Gadgets.SingleAsync(g => g.Id == id);
            gadget.Rename("New name");
            await write.SaveChangesAsync();
        }

        await using var read = db.NewContext();
        var entry = await read.AuditEntries
            .Where(e => e.Action == "gadget.modified")
            .SingleAsync();

        var changes = entry.Changes();

        Assert.Single(changes);
        Assert.Equal(("Old name", "New name"), changes["Name"]);
    }

    /// <summary>
    /// Saving a record without altering it is not an event. Recording it teaches
    /// people that most entries mean nothing, which is how a trail stops being read.
    /// </summary>
    [Fact]
    public async Task Saving_without_changing_anything_records_nothing()
    {
        await using var db = await DatabaseFixture.CreateAsync(Actor, "Charity");

        Guid id;

        await using (var write = db.NewContext())
        {
            var gadget = new Gadget("Unchanged");
            write.Gadgets.Add(gadget);
            await write.SaveChangesAsync();
            id = gadget.Id;
        }

        await using (var write = db.NewContext())
        {
            var gadget = await write.Gadgets.SingleAsync(g => g.Id == id);
            gadget.TouchWithoutChanging();
            await write.SaveChangesAsync();
        }

        await using var read = db.NewContext();

        Assert.Equal(1, await read.AuditEntries.CountAsync());
    }

    /// <summary>
    /// The audit table is readable by more people than the record it describes.
    /// A secret copied into it is a secret leaked sideways.
    /// </summary>
    [Fact]
    public async Task Excluded_fields_never_reach_the_trail()
    {
        await using var db = await DatabaseFixture.CreateAsync(Actor, "Charity");

        await using (var write = db.NewContext())
        {
            var gadget = new Gadget("Thing");
            gadget.SetSecret("correct-horse-battery");
            write.Gadgets.Add(gadget);
            await write.SaveChangesAsync();
        }

        await using var read = db.NewContext();
        var entry = await read.AuditEntries.SingleAsync();

        Assert.DoesNotContain("Secret", entry.After!.Keys);
        Assert.DoesNotContain(
            "correct-horse-battery",
            string.Join('|', entry.After!.Values.Where(v => v is not null)));
    }

    /// <summary>Opt-in. Auditing everything produces a trail nobody reads.</summary>
    [Fact]
    public async Task An_unmarked_entity_is_not_audited()
    {
        await using var db = await DatabaseFixture.CreateAsync(Actor, "Charity");

        await using (var write = db.NewContext())
        {
            write.Trinkets.Add(new Trinket("Not interesting"));
            await write.SaveChangesAsync();
        }

        await using var read = db.NewContext();

        Assert.Equal(0, await read.AuditEntries.CountAsync());
    }

    [Fact]
    public async Task Deleting_a_record_keeps_what_it_was()
    {
        await using var db = await DatabaseFixture.CreateAsync(Actor, "Charity");

        Guid id;

        await using (var write = db.NewContext())
        {
            var gadget = new Gadget("Doomed");
            write.Gadgets.Add(gadget);
            await write.SaveChangesAsync();
            id = gadget.Id;
        }

        await using (var write = db.NewContext())
        {
            write.Gadgets.Remove(await write.Gadgets.SingleAsync(g => g.Id == id));
            await write.SaveChangesAsync();
        }

        await using var read = db.NewContext();
        var entry = await read.AuditEntries.SingleAsync(e => e.Action == "gadget.deleted");

        // The entry outlives the record, and is now the only evidence it existed.
        Assert.Equal("Doomed", entry.Before!["Name"]);
        Assert.Null(entry.After);
        Assert.Equal(0, await read.Gadgets.CountAsync());
    }

    /// <summary>
    /// Not read-only by omission. If some future code path tries, it fails.
    /// </summary>
    [Fact]
    public async Task An_entry_cannot_be_edited()
    {
        await using var db = await DatabaseFixture.CreateAsync(Actor, "Charity");

        await using (var write = db.NewContext())
        {
            write.Gadgets.Add(new Gadget("Thing"));
            await write.SaveChangesAsync();
        }

        await using var tamper = db.NewContext();
        var entry = await tamper.AuditEntries.SingleAsync();
        tamper.Entry(entry).Property(e => e.Reason).CurrentValue = "Something else entirely";

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tamper.SaveChangesAsync());

        Assert.Contains("append-only", thrown.Message);
    }

    [Fact]
    public async Task An_entry_cannot_be_deleted()
    {
        await using var db = await DatabaseFixture.CreateAsync(Actor, "Charity");

        await using (var write = db.NewContext())
        {
            write.Gadgets.Add(new Gadget("Thing"));
            await write.SaveChangesAsync();
        }

        await using var tamper = db.NewContext();
        tamper.AuditEntries.Remove(await tamper.AuditEntries.SingleAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(() => tamper.SaveChangesAsync());
    }

    /// <summary>
    /// Work with no human behind it — a job, a webhook — records honestly rather
    /// than inventing a user that looks like a person.
    /// </summary>
    [Fact]
    public async Task Unattributed_work_records_no_actor()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        await using (var write = db.NewContext())
        {
            write.Gadgets.Add(new Gadget("From a scheduled job"));
            await write.SaveChangesAsync();
        }

        await using var read = db.NewContext();
        var entry = await read.AuditEntries.SingleAsync();

        Assert.Null(entry.ActorId);
        Assert.Null(entry.ActorName);
    }

    [Fact]
    public void An_entry_must_say_what_happened_and_to_what()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.Throws<ArgumentException>(
            () => AuditEntry.Record(" ", "Gadget", Guid.NewGuid(), now));

        Assert.Throws<ArgumentException>(
            () => AuditEntry.Record("gadget.added", " ", Guid.NewGuid(), now));
    }
}
