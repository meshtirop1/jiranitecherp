using JiranisokoTech.Domain.Automation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiranisokoTech.Infrastructure.Persistence.Configurations;

public sealed class AutomationRuleConfiguration : IEntityTypeConfiguration<AutomationRule>
{
    public void Configure(EntityTypeBuilder<AutomationRule> builder)
    {
        builder.ToTable("automation_rules");

        builder.HasKey(one => one.Id);

        builder.Property(one => one.Name).HasMaxLength(200).IsRequired();
        builder.Property(one => one.Description).HasMaxLength(2000);
        builder.Property(one => one.Trigger).HasMaxLength(200).IsRequired();
        builder.Property(one => one.TemplateKey).HasMaxLength(100);

        builder.Ignore(one => one.IsTemplate);

        // The matcher's only query: the rules that are on, for this event.
        builder.HasIndex(one => new { one.Trigger, one.IsOn });

        /*
         * One rule per shipped template. Nullable on purpose, and it is the one case where the
         * nulls-are-distinct behaviour of a unique index is the rule itself: every rule somebody
         * wrote from nothing has no key and there may be any number of them, while two copies
         * of one template would each fire, doing the firm's process twice.
         */
        builder.HasIndex(one => one.TemplateKey).IsUnique();

        builder.OwnsMany(one => one.Conditions, condition =>
        {
            condition.ToTable("automation_conditions");
            condition.WithOwner().HasForeignKey("RuleId");

            // A GUIDv7 the domain assigned — see OwnedKeysComeFromTheDomain.
            condition.HasKey(one => one.Id);

            condition.Property(one => one.Field).HasMaxLength(100).IsRequired();
            condition.Property(one => one.Operator).HasConversion<int>().IsRequired();
            condition.Property(one => one.Value).HasMaxLength(500);
        });

        builder.OwnsMany(one => one.Actions, action =>
        {
            action.ToTable("automation_actions");
            action.WithOwner().HasForeignKey("RuleId");

            action.HasKey(one => one.Id);

            action.Property(one => one.Kind).HasConversion<int>().IsRequired();
            action.Property(one => one.Text).HasMaxLength(300);
            action.Property(one => one.Body).HasMaxLength(4000);
            action.Property(one => one.Who).HasMaxLength(200);
            action.Property(one => one.Where).HasMaxLength(200);
        });
    }
}

public sealed class AutomationRunConfiguration : IEntityTypeConfiguration<AutomationRun>
{
    public void Configure(EntityTypeBuilder<AutomationRun> builder)
    {
        builder.ToTable("automation_runs");

        builder.HasKey(one => one.Id);

        builder.Property(one => one.RuleName).HasMaxLength(200).IsRequired();
        builder.Property(one => one.Trigger).HasMaxLength(200).IsRequired();
        builder.Property(one => one.Payload).IsRequired();
        builder.Property(one => one.Summary).HasMaxLength(500).IsRequired();
        builder.Property(one => one.Chain).HasMaxLength(400);
        builder.Property(one => one.Status).HasConversion<int>().IsRequired();
        builder.Property(one => one.Error).HasMaxLength(2000);

        builder.Ignore(one => one.ChainIds);
        builder.Ignore(one => one.IsSettled);

        /*
         * One run per rule per event. The outbox delivers at least once, and the matcher runs
         * again whenever any other handler of the same event fails — so without this a rule
         * would raise its work a second time every time an unrelated handler had a bad minute.
         * The matcher checks before inserting; this is what makes the check a guarantee.
         */
        builder.HasIndex(one => new { one.RuleId, one.SourceMessageId }).IsUnique();

        // The rule page's history, newest first, and the hourly count the fan-out guard takes.
        builder.HasIndex(one => new { one.RuleId, one.MatchedAt });

        // The delayed-run job's query: what is waiting and due.
        builder.HasIndex(one => new { one.Status, one.DueAt });

        /*
         * Cascade from the rule. A rule with runs cannot be deleted through the service — it is
         * switched off instead, so its history keeps a rule to belong to — and the cascade is
         * there so that the one path that does delete, a rule that never fired, leaves nothing.
         */
        builder.HasOne<AutomationRule>()
            .WithMany()
            .HasForeignKey(one => one.RuleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.OwnsMany(one => one.Steps, step =>
        {
            step.ToTable("automation_run_steps");
            step.WithOwner().HasForeignKey("RunId");

            step.HasKey(one => one.Id);

            step.Property(one => one.Describing).HasMaxLength(300).IsRequired();
            step.Property(one => one.Outcome).HasMaxLength(1000);

            step.Ignore(one => one.IsDone);
        });
    }
}
