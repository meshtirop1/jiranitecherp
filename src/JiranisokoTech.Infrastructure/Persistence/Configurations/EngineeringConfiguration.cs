using JiranisokoTech.Domain.Engineering;
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
