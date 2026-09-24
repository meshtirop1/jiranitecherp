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

        // Money is what the code works with; the column is a count of minor units
        // beside its currency, as a contract's value is.
        builder.Ignore(project => project.Budget);
        builder.Property(project => project.BudgetCurrency).HasMaxLength(3);

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

        builder.Property(item => item.Kind).HasConversion<int>().IsRequired();

        builder.Ignore(item => item.HasUnmetCriteria);

        /*
         * The backlog's own query: everything in no sprint, unfinished, biggest first. It runs
         * every time anybody opens the planning screen, which is the screen the whole section is
         * about.
         */
        builder.HasIndex(item => new { item.SprintId, item.Status, item.Kind })
            .HasDatabaseName("IX_work_items_backlog");

        builder.HasIndex(item => item.ParentId);

        /*
         * SetNull on the parent, not Cascade. Deleting an epic must not delete the work under it
         * — the stories are where the record of what was actually done lives, and an epic is a
         * heading. They become top-level, which is visibly wrong and therefore fixable, unlike
         * being gone.
         */
        builder.HasOne<WorkItem>()
            .WithMany()
            .HasForeignKey(item => item.ParentId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<Sprint>()
            .WithMany()
            .HasForeignKey(item => item.SprintId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.OwnsMany(item => item.Labels, label =>
        {
            label.ToTable("work_item_labels");
            label.WithOwner().HasForeignKey("WorkItemId");

            label.HasKey(one => one.Id);

            label.Property(one => one.Text).HasMaxLength(Slug.MaximumLength).IsRequired();

            // One of each per card, and the text is reduced on the way in so this constraint
            // catches what it looks like it catches rather than only exact repeats.
            label.HasIndex("WorkItemId", "Text").IsUnique();

            // The question a label exists to answer: everything tagged this.
            label.HasIndex(one => one.Text);
        });

        builder.OwnsMany(item => item.Comments, comment =>
        {
            comment.ToTable("work_item_comments");
            comment.WithOwner().HasForeignKey("WorkItemId");

            comment.HasKey(one => one.Id);

            comment.Property(one => one.Body).HasMaxLength(10_000).IsRequired();
            comment.Property(one => one.At).IsRequired();

            comment.Ignore(one => one.WasEdited);
        });

        builder.OwnsMany(item => item.DoneWhen, line =>
        {
            line.ToTable("work_item_done_when");
            line.WithOwner().HasForeignKey("WorkItemId");

            line.HasKey(one => one.Id);

            line.Property(one => one.Text).HasMaxLength(500).IsRequired();
            line.Property(one => one.Order).IsRequired();
            line.Property(one => one.DroppedBecause).HasMaxLength(500);

            line.Ignore(one => one.IsMet);
            line.Ignore(one => one.IsTicked);
        });
    }
}

public sealed class SprintConfiguration : IEntityTypeConfiguration<Sprint>
{
    public void Configure(EntityTypeBuilder<Sprint> builder)
    {
        builder.ToTable("sprints");

        builder.HasKey(one => one.Id);

        builder.Property(one => one.Name).HasMaxLength(120).IsRequired();
        builder.Property(one => one.Goal).HasMaxLength(1_000);
        builder.Property(one => one.State).HasConversion<int>().IsRequired();

        builder.Ignore(one => one.IsRunning);
        builder.Ignore(one => one.IsOver);
        builder.Ignore(one => one.Accepts);
        builder.Ignore(one => one.Days);

        /*
         * No unique index enforcing one running sprint, although it is tempting. A partial index
         * over State could do it in PostgreSQL, and it would be the second constraint in this
         * codebase that reads as stricter than it is — the rule is "at most one row with state 2",
         * which a plain index cannot say, and the service refusal can name the sprint that is
         * already running. An index would produce a constraint violation naming a column.
         */
        builder.HasIndex(one => one.State);
        builder.HasIndex(one => one.Starts);
    }
}

public sealed class WorkItemLinkConfiguration : IEntityTypeConfiguration<WorkItemLink>
{
    public void Configure(EntityTypeBuilder<WorkItemLink> builder)
    {
        builder.ToTable("work_item_links");

        builder.HasKey(one => one.Id);

        // One link per pair per direction. The reverse pair is a different row and a different
        // claim, and the service refuses it as a circle rather than the database as a duplicate.
        builder.HasIndex(one => new { one.BlockerId, one.BlockedId }).IsUnique();

        builder.HasIndex(one => one.BlockedId);

        /*
         * Cascade from both sides, unusually for this codebase. A dependency is a fact about a
         * pair, and with one of the pair gone it is not a weakened fact but a meaningless one —
         * unlike the work itself, which is a record of what somebody did.
         */
        builder.HasOne<WorkItem>()
            .WithMany()
            .HasForeignKey(one => one.BlockerId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<WorkItem>()
            .WithMany()
            .HasForeignKey(one => one.BlockedId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
