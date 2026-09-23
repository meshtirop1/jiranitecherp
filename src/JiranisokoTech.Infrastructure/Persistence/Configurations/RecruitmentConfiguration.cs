using JiranisokoTech.Domain.Common;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Recruitment;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiranisokoTech.Infrastructure.Persistence.Configurations;

public sealed class JobRequisitionConfiguration : IEntityTypeConfiguration<JobRequisition>
{
    public void Configure(EntityTypeBuilder<JobRequisition> builder)
    {
        builder.ToTable("job_requisitions");

        builder.HasKey(requisition => requisition.Id);

        builder.Property(requisition => requisition.JobTitle).HasMaxLength(200).IsRequired();
        builder.Property(requisition => requisition.Justification).HasMaxLength(4000).IsRequired();
        builder.Property(requisition => requisition.Outcome).HasMaxLength(2000);
        builder.Property(requisition => requisition.Status).HasConversion<int>().IsRequired();

        builder.Ignore(requisition => requisition.Remaining);
        builder.Ignore(requisition => requisition.CanHire);

        builder.HasOne<Department>()
            .WithMany()
            .HasForeignKey(requisition => requisition.DepartmentId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(requisition => requisition.Status);
    }
}

public sealed class JobPostingConfiguration : IEntityTypeConfiguration<JobPosting>
{
    public void Configure(EntityTypeBuilder<JobPosting> builder)
    {
        builder.ToTable("job_postings");

        builder.HasKey(posting => posting.Id);

        builder.Property(posting => posting.Title).HasMaxLength(200).IsRequired();
        builder.Property(posting => posting.Slug).HasMaxLength(Slug.MaximumLength).IsRequired();
        builder.Property(posting => posting.Summary).HasMaxLength(600).IsRequired();
        builder.Property(posting => posting.Description).HasMaxLength(20000).IsRequired();
        builder.Property(posting => posting.Location).HasMaxLength(200);
        builder.Property(posting => posting.Status).HasConversion<int>().IsRequired();

        builder.Ignore(posting => posting.IsOpen);

        // The advert answers at this address, and gets linked to from outside.
        builder.HasIndex(posting => posting.Slug).IsUnique();

        /*
         * The requisition is not deleted while an advert points at it: an advert
         * for a post nobody can account for is worse than a delete that is
         * refused. Requisitions are closed rather than removed anyway, so this
         * only ever bites somebody clearing rows by hand.
         */
        builder.HasOne<JobRequisition>()
            .WithMany()
            .HasForeignKey(posting => posting.RequisitionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(posting => new { posting.Status, posting.PublishedAt });
    }
}

public sealed class CandidateConfiguration : IEntityTypeConfiguration<Candidate>
{
    public void Configure(EntityTypeBuilder<Candidate> builder)
    {
        builder.ToTable("candidates");

        builder.HasKey(candidate => candidate.Id);

        builder.Property(candidate => candidate.FullName).HasMaxLength(200).IsRequired();
        builder.Property(candidate => candidate.Email).HasMaxLength(255).IsRequired();
        builder.Property(candidate => candidate.Phone).HasMaxLength(40);

        builder.Property(candidate => candidate.Portfolio).HasMaxLength(500);
        builder.Property(candidate => candidate.GitHub).HasMaxLength(500);
        builder.Property(candidate => candidate.LinkedIn).HasMaxLength(500);
        builder.Property(candidate => candidate.Education).HasMaxLength(500);
        builder.Property(candidate => candidate.ExpectedSalaryCurrency).HasMaxLength(3);

        // Longer, because this is where somebody pastes the skills section of a CV.
        builder.Property(candidate => candidate.Skills).HasMaxLength(4000);

        // Money is what the code works with; the column is a count of minor units.
        builder.Ignore(candidate => candidate.ExpectedSalary);

        // One row per person. Somebody who applies twice is the same person, and
        // a second row makes their history two halves that nobody joins up.
        builder.HasIndex(candidate => candidate.Email).IsUnique();
    }
}

public sealed class JobApplicationConfiguration : IEntityTypeConfiguration<JobApplication>
{
    public void Configure(EntityTypeBuilder<JobApplication> builder)
    {
        builder.ToTable("job_applications");

        builder.HasKey(application => application.Id);

        builder.Property(application => application.Note).HasMaxLength(4000);
        builder.Property(application => application.RejectionReason).HasMaxLength(2000);
        builder.Property(application => application.Status).HasConversion<int>().IsRequired();

        builder.Property(application => application.CvFileName).HasMaxLength(255);
        builder.Property(application => application.CvStoredName).HasMaxLength(100);

        builder.Ignore(application => application.IsLive);
        builder.Ignore(application => application.HasCv);

        builder.HasOne<JobPosting>()
            .WithMany()
            .HasForeignKey(application => application.PostingId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Candidate>()
            .WithMany()
            .HasForeignKey(application => application.CandidateId)
            .OnDelete(DeleteBehavior.Restrict);

        // Nobody applies to the same advert twice. A duplicate is a double-click
        // or an impatient second attempt, and two rows mean two people reading
        // the same person in two places.
        builder.HasIndex(application => new { application.PostingId, application.CandidateId })
            .IsUnique();

        // The screen everybody opens: one advert, its applications by state.
        builder.HasIndex(application => new { application.PostingId, application.Status });
    }
}
