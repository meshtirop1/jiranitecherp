using JiranisokoTech.Domain.Incidents;
using JiranisokoTech.Domain.People;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiranisokoTech.Infrastructure.Persistence.Configurations;

public sealed class IncidentConfiguration : IEntityTypeConfiguration<Incident>
{
    public void Configure(EntityTypeBuilder<Incident> builder)
    {
        builder.ToTable("incidents");

        builder.HasKey(one => one.Id);

        builder.Property(one => one.Number).IsRequired();
        builder.Property(one => one.Title).HasMaxLength(300).IsRequired();
        builder.Property(one => one.Severity).HasConversion<int>().IsRequired();
        builder.Property(one => one.Status).HasConversion<int>().IsRequired();
        builder.Property(one => one.Affects).HasMaxLength(500);
        builder.Property(one => one.Cause).HasMaxLength(1_000);

        builder.Ignore(one => one.IsOpen);
        builder.Ignore(one => one.IsOver);
        builder.Ignore(one => one.ToDetect);
        builder.Ignore(one => one.ToMitigate);
        builder.Ignore(one => one.ToResolve);

        /*
         * The number people say out loud, and it has to be unique for the same reason a work
         * item's is: two incidents called 14 make every reference to 14 ambiguous, including the
         * ones already written in messages and commits. The sequence comes from a read of the
         * maximum, so this index is what stops two incidents raised in the same second from both
         * taking it — which is precisely when two incidents get raised.
         */
        builder.HasIndex(one => one.Number).IsUnique();

        /*
         * "What is happening" is the question the navigation asks on every page load, and it is
         * answered by counting rows in one status. Without this it is a scan of every incident
         * the firm has ever had, on every page, for a number that is almost always zero.
         */
        builder.HasIndex(one => new { one.Status, one.StartedAt })
            .HasDatabaseName("IX_incidents_status_started");

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(one => one.LeadId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.OwnsMany(one => one.Notes, note =>
        {
            note.ToTable("incident_notes");
            note.WithOwner().HasForeignKey("IncidentId");

            // The key is a GUIDv7 the domain assigned — see the note on pull request reviews.
            note.HasKey(one => one.Id);

            note.Property(one => one.Text).HasMaxLength(4_000).IsRequired();
            note.Property(one => one.Kind).HasConversion<int>().IsRequired();

            note.Ignore(one => one.WrittenLater);

            /*
             * Ordered by when the thing happened rather than by when it was typed, because a
             * timeline read in the order things were written down is not a timeline. A line
             * entered at 14:40 about 14:02 belongs at 14:02, which is the whole reason those are
             * two columns.
             */
            note.HasIndex("IncidentId", nameof(IncidentNote.At));
        });
    }
}

public sealed class PostmortemConfiguration : IEntityTypeConfiguration<Postmortem>
{
    public void Configure(EntityTypeBuilder<Postmortem> builder)
    {
        builder.ToTable("postmortems");

        builder.HasKey(one => one.Id);

        builder.Property(one => one.Status).HasConversion<int>().IsRequired();

        /*
         * Long, and required rather than nullable. An empty string and a null both mean "not
         * written", and having two spellings of that would mean every check for it is a check
         * somebody can get wrong in one of two ways.
         */
        builder.Property(one => one.WhatHappened).HasMaxLength(10_000).IsRequired();
        builder.Property(one => one.WhyItWasPossible).HasMaxLength(10_000).IsRequired();
        builder.Property(one => one.HowItWasNoticed).HasMaxLength(10_000).IsRequired();
        builder.Property(one => one.WhatWouldHaveCaughtItSooner)
            .HasMaxLength(10_000).IsRequired();

        builder.Property(one => one.NothingToDoBecause).HasMaxLength(1_000);

        builder.Ignore(one => one.IsAgreed);
        builder.Ignore(one => one.IsWritten);

        /*
         * One review per incident. Two would be two accounts of the same event, each agreed by
         * somebody different, and there is no way to tell afterwards which one the firm meant.
         */
        builder.HasIndex(one => one.IncidentId).IsUnique();

        builder.HasOne<Incident>()
            .WithMany()
            .HasForeignKey(one => one.IncidentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.OwnsMany(one => one.Actions, action =>
        {
            action.ToTable("corrective_actions");
            action.WithOwner().HasForeignKey("PostmortemId");

            action.HasKey(one => one.Id);

            action.Property(one => one.Title).HasMaxLength(300).IsRequired();
            action.Property(one => one.Number).IsRequired();

            /*
             * No foreign key to work_items, deliberately, and it is the one place in this file
             * worth arguing with. A key with SetNull would leave an action pointing at nothing
             * and a review claiming the firm agreed to do something it can no longer name;
             * Cascade would delete the record of an agreement because somebody tidied the board.
             * Keeping the id and the number without a constraint means a deleted work item
             * leaves an action that still says what was agreed and can no longer be followed —
             * which is the truth, and is what the screen says.
             */
            action.HasIndex(one => one.WorkItemId);
        });
    }
}
