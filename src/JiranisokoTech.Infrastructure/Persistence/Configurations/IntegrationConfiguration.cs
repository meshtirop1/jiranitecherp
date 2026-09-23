using JiranisokoTech.Domain.Integrations;
using JiranisokoTech.Domain.Money;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiranisokoTech.Infrastructure.Persistence.Configurations;

public sealed class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> builder)
    {
        builder.ToTable("webhook_subscriptions");

        builder.HasKey(one => one.Id);

        builder.Property(one => one.Name).HasMaxLength(200).IsRequired();
        builder.Property(one => one.Endpoint).HasMaxLength(2000).IsRequired();
        builder.Property(one => one.DisabledReason).HasMaxLength(500);

        // No length. A protected value is the ciphertext plus the key ring's own
        // header, and its size is the framework's business rather than a number to
        // guess at here.
        builder.Property(one => one.ProtectedSecret).IsRequired();

        builder.Ignore(one => one.IsActive);

        builder.HasIndex(one => one.DisabledAt);

        builder.OwnsMany(one => one.Events, subscribed =>
        {
            subscribed.ToTable("webhook_subscription_events");
            subscribed.WithOwner().HasForeignKey("SubscriptionId");

            // The key is a GUIDv7 the domain assigned. AppDbContext's
            // OwnedKeysComeFromTheDomain is what stops EF taking that for an existing
            // row and silently never inserting it.
            subscribed.HasKey(one => one.Id);

            subscribed.Property(one => one.Name).HasMaxLength(200).IsRequired();

            /*
             * Indexed, because this is the column every published event is matched
             * against. Without it, raising an event means a scan of every event every
             * subscription has ever asked for.
             */
            subscribed.HasIndex(one => one.Name);
        });
    }
}

public sealed class OutboundDeliveryConfiguration : IEntityTypeConfiguration<OutboundDelivery>
{
    public void Configure(EntityTypeBuilder<OutboundDelivery> builder)
    {
        builder.ToTable("outbound_deliveries");

        builder.HasKey(one => one.Id);

        builder.Property(one => one.Event).HasMaxLength(100).IsRequired();
        builder.Property(one => one.Status).HasConversion<int>().IsRequired();
        builder.Property(one => one.Error).HasMaxLength(500);

        // No length: the body is whatever the event serialised to.
        builder.Property(one => one.Payload).IsRequired();

        builder.Ignore(one => one.IsWaiting);

        // The dispatcher's query: what is due, oldest first.
        builder.HasIndex(one => new { one.Status, one.NextAttemptAt, one.QueuedAt });

        /*
         * Cascaded, unlike an incoming delivery which survives its repository. The
         * difference is what each one is evidence of: an incoming delivery is the
         * record of what arrived and is worth keeping whatever happens to the
         * repository, while an outgoing one is a queued attempt to reach an endpoint
         * that no longer exists. Keeping those would leave a queue of notifications
         * addressed to nowhere.
         */
        builder.HasOne<Subscription>()
            .WithMany()
            .HasForeignKey(one => one.SubscriptionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ExchangeRateConfiguration : IEntityTypeConfiguration<ExchangeRate>
{
    public void Configure(EntityTypeBuilder<ExchangeRate> builder)
    {
        builder.ToTable("exchange_rates");

        builder.HasKey(one => one.Id);

        builder.Property(one => one.From).HasMaxLength(3).IsRequired();
        builder.Property(one => one.To).HasMaxLength(3).IsRequired();
        builder.Property(one => one.Source).HasMaxLength(200);

        /*
         * Eighteen digits with eight after the point. A decimal rather than a double for
         * the same reason money counts minor units: 129.45 is not representable in binary
         * floating point, and a rate applied across a year of invoices compounds the
         * error into a figure somebody has to reconcile by hand.
         *
         * Eight decimal places because a weak currency against a strong one needs them —
         * one shilling in dollars is 0.0077, and two places would round it to nothing.
         */
        builder.Property(one => one.Rate).HasPrecision(18, 8).IsRequired();

        /*
         * One rate per pair per day. Two would mean a report picking whichever the query
         * returned first and producing a different total on a second reading, which is
         * the single most corrosive thing a financial report can do.
         */
        builder.HasIndex(one => new { one.From, one.To, one.On }).IsUnique();
    }
}

public sealed class JobRunConfiguration : IEntityTypeConfiguration<Scheduling.JobRun>
{
    public void Configure(EntityTypeBuilder<Scheduling.JobRun> builder)
    {
        builder.ToTable("job_runs");

        builder.HasKey(run => run.Id);

        builder.Property(run => run.Job).HasMaxLength(100).IsRequired();
        builder.Property(run => run.Outcome).HasConversion<int>().IsRequired();
        builder.Property(run => run.Detail).HasMaxLength(500).IsRequired();

        /*
         * The scheduler's own question on every sweep: when did each job last run. Without
         * this index that is a scan of the whole history, once a minute, forever.
         */
        builder.HasIndex(run => new { run.Job, run.At });
    }
}
