using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Work;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Repository = JiranisokoTech.Domain.Engineering.Repository;

namespace JiranisokoTech.Infrastructure.Persistence.Configurations;

public sealed class RepositoryConfiguration : IEntityTypeConfiguration<Repository>
{
    public void Configure(EntityTypeBuilder<Repository> builder)
    {
        builder.ToTable("repositories");

        builder.HasKey(repository => repository.Id);

        builder.Property(repository => repository.Provider).HasConversion<int>().IsRequired();
        builder.Property(repository => repository.Owner).HasMaxLength(200).IsRequired();
        builder.Property(repository => repository.Name).HasMaxLength(200).IsRequired();
        builder.Property(repository => repository.SecretHash).HasMaxLength(64).IsRequired();

        builder.Ignore(repository => repository.FullName);
        builder.Ignore(repository => repository.IsWatched);

        /*
         * Owner and name together, per provider, and unique. Every delivery is
         * matched on this, so it has to be fast — and two rows for one
         * repository would mean deliveries landing against whichever of them
         * the query happened to return first, splitting one repository's
         * history across two.
         */
        builder.HasIndex(
                repository => new { repository.Provider, repository.Owner, repository.Name })
            .IsUnique();

        builder.HasOne<Project>()
            .WithMany()
            .HasForeignKey(repository => repository.ProjectId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class WebhookDeliveryConfiguration : IEntityTypeConfiguration<WebhookDelivery>
{
    public void Configure(EntityTypeBuilder<WebhookDelivery> builder)
    {
        builder.ToTable("webhook_deliveries");

        builder.HasKey(delivery => delivery.Id);

        builder.Property(delivery => delivery.Provider).HasConversion<int>().IsRequired();
        builder.Property(delivery => delivery.ExternalId).HasMaxLength(200).IsRequired();
        builder.Property(delivery => delivery.Event).HasMaxLength(100).IsRequired();
        builder.Property(delivery => delivery.Status).HasConversion<int>().IsRequired();
        builder.Property(delivery => delivery.Error).HasMaxLength(1000);

        // No length. A pull request payload with a long description runs past
        // any figure that would look generous here, and a truncated body is a
        // body that cannot be replayed.
        builder.Property(delivery => delivery.Payload).IsRequired();

        builder.Ignore(delivery => delivery.IsWaiting);
        builder.Ignore(delivery => delivery.NeedsSomebody);

        /*
         * The index that carries the whole idempotency guarantee, and with it
         * the replay protection. Not a convenience: the duplicate check in the
         * inbox races itself under concurrent redelivery, and this is what
         * actually refuses the second copy.
         */
        builder.HasIndex(delivery => new { delivery.Provider, delivery.ExternalId })
            .IsUnique();

        // The dispatcher's query: what is waiting, oldest first.
        builder.HasIndex(delivery => new { delivery.Status, delivery.ReceivedAt });

        /*
         * A delivery survives the repository being disconnected and deleted.
         * It is the evidence of what arrived and what was done with it, and an
         * audit record that disappears when somebody tidies up is not one.
         */
        builder.HasOne<Repository>()
            .WithMany()
            .HasForeignKey(delivery => delivery.RepositoryId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class PullRequestConfiguration : IEntityTypeConfiguration<PullRequest>
{
    public void Configure(EntityTypeBuilder<PullRequest> builder)
    {
        builder.ToTable("pull_requests");

        builder.HasKey(pullRequest => pullRequest.Id);

        builder.Property(pullRequest => pullRequest.Title).HasMaxLength(500).IsRequired();
        builder.Property(pullRequest => pullRequest.Branch).HasMaxLength(300).IsRequired();
        builder.Property(pullRequest => pullRequest.Author).HasMaxLength(200).IsRequired();
        builder.Property(pullRequest => pullRequest.State).HasConversion<int>().IsRequired();

        builder.Ignore(pullRequest => pullRequest.IsOpen);
        builder.Ignore(pullRequest => pullRequest.IsApproved);

        // The provider numbers pull requests per repository, so the pair is the
        // natural key a delivery arrives with.
        builder.HasIndex(pullRequest => new { pullRequest.RepositoryId, pullRequest.Number })
            .IsUnique();

        builder.HasIndex(pullRequest => pullRequest.WorkItemId);

        builder.HasOne<Repository>()
            .WithMany()
            .HasForeignKey(pullRequest => pullRequest.RepositoryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<WorkItem>()
            .WithMany()
            .HasForeignKey(pullRequest => pullRequest.WorkItemId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.OwnsMany(pullRequest => pullRequest.Reviews, review =>
        {
            review.ToTable("pull_request_reviews");
            review.WithOwner().HasForeignKey("PullRequestId");

            // The key is a GUIDv7 the domain assigned. AppDbContext's
            // OwnedKeysComeFromTheDomain is what stops EF taking that for an
            // existing row and silently never inserting it.
            review.HasKey(one => one.Id);

            review.Property(one => one.ExternalId).HasMaxLength(200).IsRequired();
            review.Property(one => one.Reviewer).HasMaxLength(200).IsRequired();
            review.Property(one => one.Verdict).HasConversion<int>().IsRequired();
        });
    }
}

public sealed class CommitConfiguration : IEntityTypeConfiguration<Commit>
{
    public void Configure(EntityTypeBuilder<Commit> builder)
    {
        builder.ToTable("commits");

        builder.HasKey(commit => commit.Id);

        builder.Property(commit => commit.Sha).HasMaxLength(64).IsRequired();
        builder.Property(commit => commit.Message).HasMaxLength(1000).IsRequired();
        builder.Property(commit => commit.Author).HasMaxLength(200).IsRequired();
        builder.Property(commit => commit.Branch).HasMaxLength(300).IsRequired();

        builder.Ignore(commit => commit.Short);

        /*
         * Unique across every repository rather than within one. A hash
         * identifies a commit globally, and the same commit really can appear
         * in two repositories — a fork, or a shared history. Storing it twice
         * would double the evidence of one piece of work, which is the specific
         * thing this integration must not do.
         */
        builder.HasIndex(commit => commit.Sha).IsUnique();

        builder.HasIndex(commit => commit.WorkItemId);

        builder.HasOne<Repository>()
            .WithMany()
            .HasForeignKey(commit => commit.RepositoryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<WorkItem>()
            .WithMany()
            .HasForeignKey(commit => commit.WorkItemId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class BuildConfiguration : IEntityTypeConfiguration<Build>
{
    public void Configure(EntityTypeBuilder<Build> builder)
    {
        builder.ToTable("builds");

        builder.HasKey(build => build.Id);

        builder.Property(build => build.ExternalId).HasMaxLength(100).IsRequired();
        builder.Property(build => build.Name).HasMaxLength(200).IsRequired();
        builder.Property(build => build.Sha).HasMaxLength(64).IsRequired();
        builder.Property(build => build.Branch).HasMaxLength(300).IsRequired();
        builder.Property(build => build.Outcome).HasConversion<int>().IsRequired();
        builder.Property(build => build.Url).HasMaxLength(500);

        /*
         * Ignored, all three, and this is not tidiness. A computed property that is not
         * ignored becomes a real column: EF maps it, the migration creates it, every save
         * writes it and nothing ever reads it — and no test fails, because the model and
         * the snapshot agree about the mistake.
         */
        builder.Ignore(build => build.IsRunning);
        builder.Ignore(build => build.Took);

        /*
         * Unique within a repository, on the host's own run identifier. This is what makes a
         * run reported three times — requested, in progress, completed — one row instead of
         * three, two of which would say a passed build was still going.
         *
         * Within a repository rather than globally, unlike a commit's hash: a run identifier
         * is a counter on the host's side of the fence, and two repositories will reuse the
         * same small numbers within a week of being connected.
         */
        builder.HasIndex(build => new { build.RepositoryId, build.ExternalId }).IsUnique();

        /*
         * The commit, because "was this built" is asked of a sha — by the work item panel
         * and by a person who has just pushed. The work item, because the panel asks the
         * other way round as well.
         */
        builder.HasIndex(build => build.Sha);

        builder.HasIndex(build => build.WorkItemId);

        builder.HasOne<Repository>()
            .WithMany()
            .HasForeignKey(build => build.RepositoryId)
            .OnDelete(DeleteBehavior.Cascade);

        /*
         * Set to null rather than cascading, matching a commit. A build is evidence that
         * something was built and it outlives the task it was attached to; deleting the task
         * should lose the link, not the fact.
         */
        builder.HasOne<WorkItem>()
            .WithMany()
            .HasForeignKey(build => build.WorkItemId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class DeploymentConfiguration : IEntityTypeConfiguration<Deployment>
{
    public void Configure(EntityTypeBuilder<Deployment> builder)
    {
        builder.ToTable("deployments");

        builder.HasKey(one => one.Id);

        builder.Property(one => one.ExternalId).HasMaxLength(100).IsRequired();
        builder.Property(one => one.Environment).HasConversion<int>().IsRequired();
        builder.Property(one => one.EnvironmentName).HasMaxLength(100).IsRequired();
        builder.Property(one => one.Sha).HasMaxLength(64).IsRequired();
        builder.Property(one => one.Branch).HasMaxLength(300);
        builder.Property(one => one.DeployedBy).HasMaxLength(200);
        builder.Property(one => one.State).HasConversion<int>().IsRequired();
        builder.Property(one => one.Url).HasMaxLength(500);

        builder.Ignore(one => one.IsRunning);
        builder.Ignore(one => one.Live);
        builder.Ignore(one => one.Took);

        /*
         * The same uniqueness as a build, and it matters more here. GitHub sends a
         * deployment event and then one or more deployment_status events carrying the same
         * deployment identifier, so without this every release appears three or four times —
         * on the one screen in this system whose entire job is to say what is live.
         */
        builder.HasIndex(one => new { one.RepositoryId, one.ExternalId }).IsUnique();

        /*
         * The environments page reads "what is in production, most recent first", and the
         * timesheet reads "what went out on this day". Both are answered by this.
         *
         * Named explicitly because the generated name for a two-column index on a table
         * called deployments runs close to PostgreSQL's 63-character identifier limit, and a
         * truncated name is how two indexes silently become one.
         */
        builder.HasIndex(one => new { one.Environment, one.At })
            .HasDatabaseName("IX_deployments_environment_at");

        builder.HasIndex(one => one.Sha);

        builder.HasIndex(one => one.WorkItemId);

        builder.HasOne<Repository>()
            .WithMany()
            .HasForeignKey(one => one.RepositoryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<WorkItem>()
            .WithMany()
            .HasForeignKey(one => one.WorkItemId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class ContributorConfiguration : IEntityTypeConfiguration<Contributor>
{
    public void Configure(EntityTypeBuilder<Contributor> builder)
    {
        builder.ToTable("contributors");

        builder.HasKey(one => one.Id);

        builder.Property(one => one.Provider).HasConversion<int>().IsRequired();
        builder.Property(one => one.Handle).HasMaxLength(200).IsRequired();

        /*
         * One handle belongs to one person. Two rows for the same login would mean a
         * commit attributed to whichever the query returned first, which is a
         * timesheet showing somebody else's work and no error anywhere.
         */
        builder.HasIndex(one => new { one.Provider, one.Handle }).IsUnique();

        builder.HasIndex(one => one.EmployeeId);

        /*
         * Cascaded, unlike most links to an employee. A claim is only meaningful as a
         * statement that this login is that person; with the person gone it is not a
         * fact about anything, and the commits themselves keep the login they were
         * made under regardless.
         */
        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(one => one.EmployeeId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
