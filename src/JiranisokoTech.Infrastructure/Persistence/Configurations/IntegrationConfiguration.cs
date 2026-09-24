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

        builder.OwnsMany(one => one.Wanted, subscribed =>
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
        builder.Property(run => run.AskedBy).HasMaxLength(200);

        builder.Ignore(run => run.WasScheduled);

        /*
         * The scheduler's own question on every sweep: when did each job last run. Without
         * this index that is a scan of the whole history, once a minute, forever.
         *
         * AskedBy is not in it, although both the sweep and the overdue check now filter
         * on "the scheduler ran it". On-demand runs are a handful of rows in a history of
         * thousands, so the planner reads the same index entries either way and discards
         * one or two — a third column would make every entry wider to save nothing.
         */
        builder.HasIndex(run => new { run.Job, run.At });
    }
}

public sealed class ContactConfiguration : IEntityTypeConfiguration<Domain.Clients.Contact>
{
    public void Configure(EntityTypeBuilder<Domain.Clients.Contact> builder)
    {
        builder.ToTable("client_contacts");

        builder.HasKey(one => one.Id);

        builder.Property(one => one.Name).HasMaxLength(200).IsRequired();
        builder.Property(one => one.JobTitle).HasMaxLength(150);
        builder.Property(one => one.Email).HasMaxLength(255);
        builder.Property(one => one.Phone).HasMaxLength(50);

        builder.Ignore(one => one.IsHere);

        // Every read is "who is at this client", and whether they are still there.
        builder.HasIndex(one => new { one.ClientId, one.GoneAt });

        /*
         * There is deliberately no unique index on (ClientId) where IsMain, although exactly
         * one contact per client holds it and a partial unique index is how PostgreSQL says
         * so. Promoting somebody means clearing the flag on whoever has it and setting it on
         * the new one in one save, and nothing makes EF order those two UPDATEs — so the
         * constraint would fire on whichever ordering it happened to pick, intermittently,
         * with a unique-violation nobody could reproduce. The rule lives in ContactService
         * and is tested there.
         */

        builder.HasOne<Domain.Clients.Client>()
            .WithMany()
            .HasForeignKey(one => one.ClientId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class OpportunityConfiguration : IEntityTypeConfiguration<Domain.Clients.Opportunity>
{
    public void Configure(EntityTypeBuilder<Domain.Clients.Opportunity> builder)
    {
        builder.ToTable("opportunities");

        builder.HasKey(one => one.Id);

        builder.Property(one => one.Title).HasMaxLength(300).IsRequired();
        builder.Property(one => one.About).HasMaxLength(300).IsRequired();
        builder.Property(one => one.Stage).HasConversion<int>().IsRequired();
        builder.Property(one => one.ValueCurrency).HasMaxLength(3);
        builder.Property(one => one.Outcome).HasMaxLength(1000);

        builder.Ignore(one => one.Value);
        builder.Ignore(one => one.IsOpen);

        /*
         * The pipeline's own reading: what is open, and how long since it moved. Sorted by
         * silence rather than by value, because a list ordered by value shows what somebody
         * hopes for and a list ordered by silence shows what they have stopped doing.
         */
        builder.HasIndex(one => new { one.Stage, one.MovedAt });

        /*
         * Set to null rather than cascading. An opportunity outlives the client record being
         * removed, because what was won or lost and why is the firm's own history — and a
         * lost enquiry whose reason disappeared is the one piece of it worth keeping.
         */
        builder.HasOne<Domain.Clients.Client>()
            .WithMany()
            .HasForeignKey(one => one.ClientId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.OwnsMany(one => one.Activities, activity =>
        {
            activity.ToTable("opportunity_activities");
            activity.WithOwner().HasForeignKey("OpportunityId");
            activity.HasKey(one => one.Id);

            activity.Property(one => one.Kind).HasConversion<int>().IsRequired();
            activity.Property(one => one.What).HasMaxLength(2000).IsRequired();

            activity.HasIndex(one => one.At);
        });
    }
}
