using JiranisokoTech.Domain.Integrations;
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
