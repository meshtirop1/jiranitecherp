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
