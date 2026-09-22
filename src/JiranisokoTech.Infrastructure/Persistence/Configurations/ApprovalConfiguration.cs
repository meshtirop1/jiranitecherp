using JiranisokoTech.Domain.Approvals;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiranisokoTech.Infrastructure.Persistence.Configurations;

public sealed class ApprovalRequestConfiguration : IEntityTypeConfiguration<ApprovalRequest>
{
    public void Configure(EntityTypeBuilder<ApprovalRequest> builder)
    {
        builder.ToTable("approval_requests");

        builder.HasKey(request => request.Id);

        builder.Property(request => request.SubjectType).HasMaxLength(100).IsRequired();
        builder.Property(request => request.Action).HasMaxLength(100).IsRequired();
        builder.Property(request => request.Outcome).HasMaxLength(2000);
        builder.Property(request => request.Status).HasConversion<int>().IsRequired();

        builder.Ignore(request => request.IsSettled);
        builder.Ignore(request => request.CurrentStep);
        builder.Ignore(request => request.WaitingOn);

        /*
         * The steps are owned by the request, not related to it.
         *
         * They are loaded with it, saved with it and deleted with it, and they
         * are never queried on their own — which is the definition EF uses, and
         * also the rule the domain depends on. A step reachable without its
         * chain is a step somebody can decide without the chain checking whether
         * it was their turn.
         */
        builder.OwnsMany(request => request.Steps, step =>
        {
            step.ToTable("approval_steps");

            step.WithOwner().HasForeignKey("ApprovalRequestId");
            step.HasKey(one => one.Id);

            step.Property(one => one.Order).IsRequired();
            step.Property(one => one.Status).HasConversion<int>().IsRequired();
            step.Property(one => one.Note).HasMaxLength(2000);

            step.Ignore(one => one.IsWaiting);

            // One place in the chain, once.
            step.HasIndex("ApprovalRequestId", nameof(ApprovalStep.Order)).IsUnique();

            // The query behind "what is waiting on me?", which every person in
            // the firm runs whenever they open the application.
            step.HasIndex(one => new { one.DeciderId, one.Status });
        });

        // Navigating from a requisition or an expense to its approvals.
        builder.HasIndex(request => new { request.SubjectType, request.SubjectId });

        // And the administrator view: everything still waiting, oldest first.
        builder.HasIndex(request => new { request.Status, request.RequestedAt });
    }
}
