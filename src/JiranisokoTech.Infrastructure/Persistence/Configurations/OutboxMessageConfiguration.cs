using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiranisokoTech.Infrastructure.Persistence.Configurations;

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");

        builder.HasKey(message => message.Id);

        builder.Property(message => message.Type).HasMaxLength(200).IsRequired();
        builder.Property(message => message.Payload).IsRequired();
        builder.Property(message => message.Error).HasMaxLength(2000);
        builder.Property(message => message.OccurredAt).IsRequired();

        /*
         * The dispatcher's only query: the oldest messages not yet sent.
         *
         * Composite rather than partial. A partial index on "dispatched is
         * null" would be smaller still — the table is append-mostly and grows
         * forever, while the backlog stays short — but the filter is written in
         * raw SQL per provider, and the suite runs on SQLite while production
         * runs on PostgreSQL. This ordering lets the planner seek the nulls and
         * read them already in time order, which is the behaviour that matters.
         *
         * Worth revisiting as a PostgreSQL-only partial index once the table is
         * large enough for the difference to be measurable.
         */
        builder.HasIndex(message => new { message.DispatchedAt, message.OccurredAt })
            .HasDatabaseName("ix_outbox_pending");
    }
}
