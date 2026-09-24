using JiranisokoTech.Domain.Notices;
using JiranisokoTech.Domain.People;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiranisokoTech.Infrastructure.Persistence.Configurations;

public sealed class NoticeConfiguration : IEntityTypeConfiguration<Notice>
{
    public void Configure(EntityTypeBuilder<Notice> builder)
    {
        builder.ToTable("notices");

        builder.HasKey(one => one.Id);

        builder.Property(one => one.Kind).HasConversion<int>().IsRequired();
        builder.Property(one => one.Subject).HasMaxLength(500).IsRequired();
        builder.Property(one => one.Link).HasMaxLength(500);

        builder.Ignore(one => one.IsUnread);

        /*
         * Two questions and one index. "How many has this person not read" runs on every page
         * load for the navigation, and "what are this person's, newest first" runs when they
         * open the centre. Both start from the person, and the read timestamp narrows the
         * first to almost nothing.
         */
        builder.HasIndex(one => new { one.ForEmployeeId, one.ReadAt, one.At })
            .HasDatabaseName("IX_notices_for_read_at");

        /*
         * Cascade, unusually. A notice is addressed to one person and means nothing without
         * them — it is not a record of what the firm did, which is what the audit trail and
         * every other history here is for. A row saying "you were given something" belonging
         * to nobody is not evidence of anything.
         */
        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(one => one.ForEmployeeId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class NoticeRuleConfiguration : IEntityTypeConfiguration<NoticeRule>
{
    public void Configure(EntityTypeBuilder<NoticeRule> builder)
    {
        builder.ToTable("notice_rules");

        builder.HasKey(one => one.Id);

        builder.Property(one => one.Kind).HasConversion<int>().IsRequired();

        /*
         * One row per person per kind. Two would let somebody's preference be both on and off,
         * and which one applied would depend on the order rows came back in — a setting that
         * changes by itself is worse than one that cannot be changed.
         */
        builder.HasIndex(one => new { one.EmployeeId, one.Kind }).IsUnique();

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(one => one.EmployeeId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class AnnouncementConfiguration : IEntityTypeConfiguration<Announcement>
{
    public void Configure(EntityTypeBuilder<Announcement> builder)
    {
        builder.ToTable("announcements");

        builder.HasKey(one => one.Id);

        builder.Property(one => one.Title).HasMaxLength(200).IsRequired();
        builder.Property(one => one.Body).HasMaxLength(20_000).IsRequired();
        builder.Property(one => one.State).HasConversion<int>().IsRequired();
        builder.Property(one => one.Outcome).HasMaxLength(1_100);

        builder.Ignore(one => one.IsPosted);
        builder.Ignore(one => one.HasAnybodyAcknowledged);

        /*
         * The board's own query: what is up, not expired, for the whole firm or one department.
         * It runs for everybody who opens the page, which is everybody.
         */
        builder.HasIndex(one => new { one.State, one.ExpiresOn, one.DepartmentId })
            .HasDatabaseName("IX_announcements_up");

        /*
         * Restrict on the author, not Cascade — the opposite of the decision a notice makes one
         * class above, and for the opposite reason. A notice addressed to nobody is evidence of
         * nothing; an announcement is the firm speaking, and it is evidence regardless of who
         * typed it, so deleting their staff record must not take it away.
         *
         * Restrict rather than SetNull because the column is not nullable and should not be: an
         * unattributed announcement is a notice board with anonymous posts on it, which is a
         * rumour mill. Nothing in this system deletes an employee anyway — people leave, and
         * leaving keeps the row — so the restriction costs nothing and says what it means.
         */
        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(one => one.ByEmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Department>()
            .WithMany()
            .HasForeignKey(one => one.DepartmentId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.OwnsMany(one => one.Acknowledgements, said =>
        {
            said.ToTable("announcement_acknowledgements");
            said.WithOwner().HasForeignKey("AnnouncementId");

            said.HasKey(row => row.Id);

            said.Property(row => row.EmployeeId).IsRequired();
            said.Property(row => row.At).IsRequired();

            /*
             * One per person per announcement. Unlike a team membership this one IS expressible
             * as a unique index, because neither column is nullable — and it is worth having,
             * because the button sits on a page people reload and a double row would make more
             * people appear to have answered than did.
             */
            said.HasIndex("AnnouncementId", "EmployeeId").IsUnique();
        });
    }
}
