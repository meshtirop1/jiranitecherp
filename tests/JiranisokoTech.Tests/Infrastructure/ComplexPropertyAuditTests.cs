using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Tests.Infrastructure;

/// <summary>
/// Changes inside a complex property, and what the trail says about them.
/// </summary>
/// <remarks>
/// <b>These exist because of a live fault that had been in the trail for weeks.</b>
///
/// <c>PersonalDetails</c>, <c>EmergencyContact</c> and <c>Terms</c> are mapped with
/// <c>ComplexProperty</c>, and every loop in <c>AppDbContext.CaptureAudit</c> walked
/// <c>entry.Properties</c>, which does not enumerate a complex type's members. So editing
/// somebody's salary, their phone number or their next of kin produced no audit entry at
/// all — not a redacted one, none — because a modification whose "after" comes back empty is
/// deliberately discarded as "nothing moved". The section 29 row claimed a log of every
/// change while three of the most sensitive columns in the database sat outside it.
///
/// <c>Employee.AuditExcludes</c> named two of those three, which made the gap read as
/// intentional to anybody checking. It was intentional about the <i>values</i> and accidental
/// about the <i>act</i>, and those are not the same decision: keeping a salary figure out of
/// an append-only table nothing prunes is right, and leaving no record that somebody changed
/// it is the opposite of what a trail is for.
///
/// Found by a throwaway test written to settle the question, after an agent reading the
/// configuration noticed the mapping and said the exclusion list could not mean what it said.
/// </remarks>
public class ComplexPropertyAuditTests
{
    private static readonly Guid Actor = Guid.CreateVersion7();

    /// <summary>
    /// An excluded complex property records that it changed, and not what to.
    /// </summary>
    /// <remarks>
    /// The whole point. Both halves are asserted, because either one alone is a fault: an
    /// entry with the figures in it puts a salary in a table nothing prunes, and no entry at
    /// all means nobody can tell that a salary was ever touched.
    /// </remarks>
    [Fact]
    public async Task Changing_a_salary_is_recorded_without_recording_the_salary()
    {
        await using var db = await DatabaseFixture.CreateAsync(Actor, "Charity Jepchirchir");

        Guid id;

        await using (var write = db.NewContext())
        {
            var gadget = new Gadget("Payment gateway");
            write.Gadgets.Add(gadget);
            await write.SaveChangesAsync();
            id = gadget.Id;
        }

        await using (var write = db.NewContext())
        {
            var gadget = await write.Gadgets.SingleAsync(one => one.Id == id);
            gadget.Rebuild(new Innards("SN-88419", "4.2.1"));
            await write.SaveChangesAsync();
        }

        await using var read = db.NewContext();

        var entry = await read.AuditEntries
            .AsNoTracking()
            .SingleAsync(one => one.Action == "gadget.modified");

        Assert.Equal(Actor, entry.ActorId);
        Assert.True(entry.After!.ContainsKey(nameof(Gadget.Innards)));

        var everything = string.Join(
            " ",
            entry.After!.Select(pair => $"{pair.Key}={pair.Value}")
                .Concat((entry.Before ?? new Dictionary<string, string?>())
                    .Select(pair => $"{pair.Key}={pair.Value}")));

        Assert.DoesNotContain("SN-88419", everything);
        Assert.DoesNotContain("4.2.1", everything);
    }

    /// <summary>
    /// A complex property the trail may keep is kept member by member.
    /// </summary>
    /// <remarks>
    /// The other branch, and nothing in the application exercises it: all three complex
    /// properties in the real model are on <c>Employee</c> and all three are excluded, which
    /// is exactly the situation where a branch rots unnoticed.
    /// </remarks>
    [Fact]
    public async Task Changing_a_complex_property_that_may_be_kept_records_the_members()
    {
        await using var db = await DatabaseFixture.CreateAsync(Actor, "Charity");

        Guid id;

        await using (var write = db.NewContext())
        {
            var gadget = new Gadget("Payment gateway");
            gadget.Reshape(new Shape(4, "grey"));
            write.Gadgets.Add(gadget);
            await write.SaveChangesAsync();
            id = gadget.Id;
        }

        await using (var write = db.NewContext())
        {
            var gadget = await write.Gadgets.SingleAsync(one => one.Id == id);
            gadget.Reshape(new Shape(6, "grey"));
            await write.SaveChangesAsync();
        }

        await using var read = db.NewContext();

        var entry = await read.AuditEntries
            .AsNoTracking()
            .SingleAsync(one => one.Action == "gadget.modified");

        Assert.Equal("4", entry.Before!["Shape.Sides"]);
        Assert.Equal("6", entry.After!["Shape.Sides"]);

        // And only what moved. The colour was assigned the same value by the whole-value
        // replacement, which is assignment rather than movement.
        Assert.False(entry.After!.ContainsKey("Shape.Colour"));
    }

    /// <summary>
    /// Changing one complex property does not claim the others changed.
    /// </summary>
    /// <remarks>
    /// Each of the three is reported on its own evidence. A single marker meaning "something
    /// inside this row changed" would be useless on a staff record, where the interesting
    /// question is never whether the row moved but which of the three did — a corrected phone
    /// number and a rewritten salary are not the same event to anybody.
    /// </remarks>
    [Fact]
    public async Task Correcting_a_phone_number_is_not_recorded_as_changing_the_salary()
    {
        await using var db = await DatabaseFixture.CreateAsync(Actor, "Charity");

        Guid id;

        await using (var write = db.NewContext())
        {
            var person = Employee.Hire("Grace Wanjiru", new DateOnly(2024, 1, 8));
            write.Employees.Add(person);
            await write.SaveChangesAsync();
            id = person.Id;
        }

        await using (var write = db.NewContext())
        {
            var person = await write.Employees.SingleAsync(one => one.Id == id);

            // The whole value reassigned, which is how every caller writes this.
            person.Record(person.Details with { Phone = "+254700000001" });

            await write.SaveChangesAsync();
        }

        await using var read = db.NewContext();

        var entry = await read.AuditEntries
            .AsNoTracking()
            .SingleAsync(one => one.Action == "employee.modified");

        Assert.True(entry.After!.ContainsKey(nameof(Employee.Details)));
        Assert.False(entry.After!.ContainsKey(nameof(Employee.Terms)));
        Assert.False(entry.After!.ContainsKey(nameof(Employee.Emergency)));
    }

    /// <summary>
    /// Assigning the same value back is not a change, and is not recorded as one.
    /// </summary>
    /// <remarks>
    /// The sibling of the scalar rule the trail has always had. A complex property is replaced
    /// wholesale here — nobody writes to one member — so this is the shape of every save that
    /// comes from a form posting all its fields back.
    ///
    /// A false entry is worse than a missing one on a field like this. It is evidence of
    /// something that never occurred, in a table that is append-only by design and therefore
    /// cannot be corrected, so somebody defending a pay decision two years later would be
    /// arguing against a record nobody can withdraw.
    ///
    /// <b>What this test cannot tell you:</b> it passes whether CaptureAudit compares the
    /// members or reads EF's modified flag, because EF 10 leaves that flag false when an equal
    /// value is reassigned. That was checked by breaking it both ways rather than assumed —
    /// and the comparison is what is written, for the reason given there. Recorded here
    /// because a test whose remark claims to guard a choice it cannot see is worse than no
    /// remark.
    /// </remarks>
    [Fact]
    public async Task Assigning_the_same_salary_back_is_not_recorded_as_a_change()
    {
        await using var db = await DatabaseFixture.CreateAsync(Actor, "Charity");

        Guid id;

        await using (var write = db.NewContext())
        {
            var gadget = new Gadget("Payment gateway");
            gadget.Rebuild(new Innards("SN-88419", "4.2.1"));
            write.Gadgets.Add(gadget);
            await write.SaveChangesAsync();
            id = gadget.Id;
        }

        await using (var write = db.NewContext())
        {
            var gadget = await write.Gadgets.SingleAsync(one => one.Id == id);

            // The same value, reassigned. This is what a form that posts every field does.
            gadget.Rebuild(new Innards("SN-88419", "4.2.1"));
            gadget.Rename("Payment gateway v2");

            await write.SaveChangesAsync();
        }

        await using var read = db.NewContext();

        var entry = await read.AuditEntries
            .AsNoTracking()
            .SingleAsync(one => one.Action == "gadget.modified");

        // The rename is there, so the entry exists and the test is not passing vacuously.
        Assert.Equal("Payment gateway v2", entry.After!["Name"]);

        Assert.False(entry.After!.ContainsKey(nameof(Gadget.Innards)));
    }

    /// <summary>
    /// A next of kin's details are the firm's business and are not the trail's.
    /// </summary>
    /// <remarks>
    /// <c>Emergency</c> was the one of the three that <c>AuditExcludes</c> did not name, so
    /// it was outside the trail by accident rather than by decision. It holds a third party's
    /// name and mobile number — somebody who does not work here, has never agreed to
    /// anything, and cannot ask what is held about them — and the trail is append-only and
    /// deliberately never pruned. Naming it makes the decision rather than inheriting it.
    /// </remarks>
    [Fact]
    public async Task A_next_of_kin_is_recorded_as_changed_and_never_quoted()
    {
        await using var db = await DatabaseFixture.CreateAsync(Actor, "Charity");

        Guid id;

        await using (var write = db.NewContext())
        {
            var person = Employee.Hire("Grace Wanjiru", new DateOnly(2024, 1, 8));
            write.Employees.Add(person);
            await write.SaveChangesAsync();
            id = person.Id;
        }

        await using (var write = db.NewContext())
        {
            var person = await write.Employees.SingleAsync(one => one.Id == id);

            person.Record(person.Emergency with
            {
                Name = "Peter Kilonzo",
                Phone = "+254700000002",
            });

            await write.SaveChangesAsync();
        }

        await using var read = db.NewContext();

        var entries = await read.AuditEntries.AsNoTracking().ToListAsync();

        var everything = string.Join(
            " ",
            entries.SelectMany(one =>
                (one.After ?? new Dictionary<string, string?>())
                    .Concat(one.Before ?? new Dictionary<string, string?>())
                    .Select(pair => $"{pair.Key}={pair.Value}")));

        Assert.Contains($"{nameof(Employee.Emergency)}=", everything);
        Assert.DoesNotContain("Peter Kilonzo", everything);
        Assert.DoesNotContain("+254700000002", everything);
    }

    /// <summary>
    /// Every complex property on an audited entity has had a decision made about it.
    /// </summary>
    /// <remarks>
    /// The guard against this fault returning in a different shape. Mapping a complex property
    /// is a quiet act — one call in a configuration file — and whoever does it is thinking
    /// about columns rather than about the trail. Without this, a future value object holding
    /// a bank account or a tax number would be written into an append-only table nobody
    /// prunes, and it would work perfectly.
    ///
    /// <c>MayBeQuoted</c> is the decision, written down and defensible one entry at a time.
    /// Both outcomes are safe — quoted in full, or recorded as changed with the values
    /// withheld — and what is refused is neither, which is somebody not having thought about
    /// it.
    /// </remarks>
    [Fact]
    public async Task Every_complex_property_on_an_audited_entity_has_had_a_decision_made()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var context = db.NewContext();

        var undecided = new List<string>();

        foreach (var type in context.Model.GetEntityTypes())
        {
            if (type.ClrType is null || !typeof(IAuditable).IsAssignableFrom(type.ClrType))
            {
                continue;
            }

            var excludes = type.ClrType
                .GetProperty(nameof(IAuditable.AuditExcludes))
                ?.GetValue(null) as IReadOnlySet<string> ?? new HashSet<string>();

            foreach (var complex in type.GetComplexProperties())
            {
                var decided = excludes.Contains(complex.Name)
                    || MayBeQuoted.Contains($"{type.ClrType.Name}.{complex.Name}");

                if (!decided)
                {
                    undecided.Add(
                        $"  {type.ClrType.Name}.{complex.Name} ({complex.ClrType.Name})");
                }
            }
        }

        Assert.True(
            undecided.Count == 0,
            "These complex properties are mapped onto audited entities and nobody has said "
            + "whether the trail may write their values down:\n"
            + string.Join('\n', undecided.Order())
            + "\n\nAdd the property to that entity's AuditExcludes, so the trail records that "
            + "it changed and withholds the values — or add it to MayBeQuoted with a reason "
            + "that would survive somebody reading the trail in five years.");
    }

    /// <summary>
    /// Complex properties whose values the trail may keep in full, with the reason.
    /// </summary>
    /// <remarks>
    /// One entry, and it is a test entity. Everything real is excluded, which is what one
    /// would expect: a value object exists because several fields belong together, and fields
    /// that belong together on a person are usually the ones nobody should be able to read out
    /// of an append-only table.
    /// </remarks>
    private static readonly HashSet<string> MayBeQuoted =
    [
        // A test stand-in for a complex property carrying nothing sensitive, so that the
        // capturing branch is exercised at all. Nothing in the application uses it.
        "Gadget.Shape",
    ];
}
