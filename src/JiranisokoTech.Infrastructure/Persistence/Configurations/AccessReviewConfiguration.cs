using JiranisokoTech.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiranisokoTech.Infrastructure.Persistence.Configurations;

public sealed class AccessReviewConfiguration : IEntityTypeConfiguration<AccessReview>
{
    public void Configure(EntityTypeBuilder<AccessReview> builder)
    {
        builder.ToTable("access_reviews");

        builder.HasKey(review => review.Id);

        builder.Property(review => review.ReviewerName).HasMaxLength(200).IsRequired();
        builder.Property(review => review.Note).HasMaxLength(2000);

        // The question asked of this table is "when was the last one", every time the security
        // centre opens.
        builder.HasIndex(review => review.ReviewedAt);

        builder.OwnsMany(review => review.Lines, line =>
        {
            line.ToTable("access_review_lines");
            line.WithOwner().HasForeignKey("AccessReviewId");

            // The key is a GUIDv7 the domain assigned, as it is for every owned row here.
            line.HasKey(one => one.Id);

            line.Property(one => one.Email).HasMaxLength(255).IsRequired();
            line.Property(one => one.Roles).HasMaxLength(500).IsRequired();

            // No foreign key to the account, deliberately: a review is kept after the accounts
            // in it are forgotten, and that is when an auditor is most likely to want it.
            line.HasIndex("AccessReviewId");
        });
    }
}
