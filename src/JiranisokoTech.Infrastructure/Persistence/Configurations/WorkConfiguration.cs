using JiranisokoTech.Domain.Common;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Work;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiranisokoTech.Infrastructure.Persistence.Configurations;

public sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.ToTable("projects");

        builder.HasKey(project => project.Id);

        builder.Property(project => project.Name).HasMaxLength(200).IsRequired();
        builder.Property(project => project.Code).HasMaxLength(Slug.MaximumLength).IsRequired();
        builder.Property(project => project.Summary).HasMaxLength(4000);
        builder.Property(project => project.Status).HasConversion<int>().IsRequired();

        builder.Ignore(project => project.IsRunning);

        // The code is what people type and quote, so two projects cannot share
        // one.
        builder.HasIndex(project => project.Code).IsUnique();
        builder.HasIndex(project => project.Status);

        builder.HasOne<Department>()
            .WithMany()
            .HasForeignKey(project => project.DepartmentId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class WorkItemConfiguration : IEntityTypeConfiguration<WorkItem>
{
    public void Configure(EntityTypeBuilder<WorkItem> builder)
    {
        builder.ToTable("work_items");

        builder.HasKey(item => item.Id);

        builder.Property(item => item.Title).HasMaxLength(300).IsRequired();
        builder.Property(item => item.Detail).HasMaxLength(8000);
        builder.Property(item => item.BlockedReason).HasMaxLength(1000);
        builder.Property(item => item.Status).HasConversion<int>().IsRequired();
        builder.Property(item => item.Priority).HasConversion<int>().IsRequired();

        builder.Ignore(item => item.IsOpen);
        builder.Ignore(item => item.Reference);

        // Unique, and indexed because every incoming commit and pull request is
        // matched against it.
        builder.HasIndex(item => item.Number).IsUnique();

        /*
         * Work outlives the project it sat under being deleted, and outlives the
         * person it was assigned to being removed. Both are set to null rather
         * than cascading: a delete that takes the work with it destroys the
         * record of what was done, which is the part anybody would want back.
         */
        builder.HasOne<Project>()
            .WithMany()
            .HasForeignKey(item => item.ProjectId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(item => item.AssigneeId)
            .OnDelete(DeleteBehavior.SetNull);

        // The two boards anybody actually opens: what is on a project, and what
        // is on one person's plate.
        builder.HasIndex(item => new { item.ProjectId, item.Status });
        builder.HasIndex(item => new { item.AssigneeId, item.Status });
        builder.HasIndex(item => item.DueOn);
    }
}
