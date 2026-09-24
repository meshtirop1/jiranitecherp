using JiranisokoTech.Domain.Privacy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiranisokoTech.Infrastructure.Persistence.Configurations;

public sealed class PrivacyRequestConfiguration : IEntityTypeConfiguration<PrivacyRequest>
{
    public void Configure(EntityTypeBuilder<PrivacyRequest> builder)
    {
        builder.ToTable("privacy_requests");

        builder.HasKey(one => one.Id);

        builder.Property(one => one.Reference).HasMaxLength(30).IsRequired();
        builder.Property(one => one.Ask).HasConversion<int>().IsRequired();
        builder.Property(one => one.SubjectKind).HasConversion<int>().IsRequired();
        builder.Property(one => one.Subject).HasMaxLength(200).IsRequired();
        builder.Property(one => one.Note).HasMaxLength(4_000);

        builder.Ignore(one => one.DueOn);
        builder.Ignore(one => one.IsAnswered);

        builder.HasIndex(one => one.Reference).IsUnique();

        /*
         * The register's own query: what is unanswered, by deadline. The clock is statutory and
         * runs whether or not anybody has looked, so this is the read that matters.
         */
        builder.HasIndex(one => new { one.AnsweredAt, one.ReceivedOn });

        /*
         * No foreign key to the subject, deliberately. The subject may be a member of staff, a
         * candidate, or somebody who is neither — and a nullable column per kind would grow every
         * time the answer to "who else do we hold data about" changed. The kind and the
         * identifier say which, and the name is stored so the register reads without a join.
         */
        builder.HasIndex(one => one.SubjectId);

        builder.OwnsMany(one => one.Outcomes, outcome =>
        {
            outcome.ToTable("privacy_outcomes");
            outcome.WithOwner().HasForeignKey("PrivacyRequestId");

            outcome.HasKey(row => row.Id);

            outcome.Property(row => row.Held).HasConversion<int>().IsRequired();
            outcome.Property(row => row.Kind).HasConversion<int>().IsRequired();
            outcome.Property(row => row.Basis).HasMaxLength(2_000);

            /*
             * One outcome per class per request, enforced here as well as in the aggregate.
             * Two answers to "what happened to their contact details" is a file where whichever
             * a reader saw first would be whichever the database returned first.
             */
            outcome.HasIndex("PrivacyRequestId", "Held").IsUnique();

            /*
             * Indexed on the retention date, because that is what a future sweep reads: a
             * retention period is only a policy until something can find the rows it has run out
             * on.
             */
            outcome.HasIndex(row => row.Until);
        });
    }
}
