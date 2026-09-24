using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Recruitment;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiranisokoTech.Infrastructure.Persistence.Configurations;

public sealed class OnboardingConfiguration : IEntityTypeConfiguration<Onboarding>
{
    public void Configure(EntityTypeBuilder<Onboarding> builder)
    {
        builder.ToTable("onboardings");

        builder.HasKey(one => one.Id);

        builder.Ignore(one => one.IsComplete);
        builder.Ignore(one => one.Outstanding);

        /*
         * One open checklist per person. Two would put the laptop on one and the sign-in on the
         * other, and whoever looked would see a short list and believe it — which is exactly the
         * reason offboarding reuses an existing one rather than starting a second.
         */
        builder.HasIndex(one => one.EmployeeId).IsUnique();

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(one => one.EmployeeId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.OwnsMany(one => one.Steps, step =>
        {
            step.ToTable("onboarding_steps");
            step.WithOwner().HasForeignKey("OnboardingId");

            // The key is a GUIDv7 the domain assigned — see the note on pull request reviews.
            step.HasKey(one => one.Id);

            step.Property(one => one.Name).HasMaxLength(200).IsRequired();
            step.Property(one => one.Order).IsRequired();

            step.Ignore(one => one.IsDone);
        });
    }
}

public sealed class OfferConfiguration : IEntityTypeConfiguration<Offer>
{
    public void Configure(EntityTypeBuilder<Offer> builder)
    {
        builder.ToTable("offers");

        builder.HasKey(one => one.Id);

        builder.Property(one => one.JobTitle).HasMaxLength(200).IsRequired();
        builder.Property(one => one.SalaryCurrency).HasMaxLength(3).IsRequired();
        builder.Property(one => one.Frequency).HasConversion<int>().IsRequired();
        builder.Property(one => one.Status).HasConversion<int>().IsRequired();
        builder.Property(one => one.Terms).HasMaxLength(10_000).IsRequired();
        builder.Property(one => one.SignedName).HasMaxLength(200);
        builder.Property(one => one.Outcome).HasMaxLength(1_000);

        /*
         * SHA-256, hex, so exactly sixty-four characters. Unique because the hash is how the
         * acceptance page finds the offer: two rows sharing one would make a link ambiguous,
         * and the one it resolved to would be whichever the database returned first.
         */
        builder.Property(one => one.TokenHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(one => one.TokenHash).IsUnique();

        builder.Ignore(one => one.Salary);
        builder.Ignore(one => one.IsOut);
        builder.Ignore(one => one.IsSettled);

        builder.HasIndex(one => one.ApplicationId);

        builder.HasOne<JobApplication>()
            .WithMany()
            .HasForeignKey(one => one.ApplicationId)
            .OnDelete(DeleteBehavior.Cascade);

        /*
         * SetNull rather than Cascade on the staff record. A person deleted from the staff list
         * must not take the record of the offer with them — the requisition it was hired
         * against, the terms and the acceptance are the firm's record of a decision, not that
         * person's row.
         */
        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(one => one.EmployeeId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
