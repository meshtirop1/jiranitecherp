using JiranisokoTech.Domain.Clients;
using JiranisokoTech.Domain.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiranisokoTech.Infrastructure.Persistence.Configurations;

public sealed class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> builder)
    {
        builder.ToTable("tickets");

        builder.HasKey(one => one.Id);

        builder.Property(one => one.Number).IsRequired();
        builder.Property(one => one.Subject).HasMaxLength(300).IsRequired();
        builder.Property(one => one.Priority).HasConversion<int>().IsRequired();
        builder.Property(one => one.Status).HasConversion<int>().IsRequired();
        builder.Property(one => one.From).HasConversion<int>().IsRequired();
        builder.Property(one => one.RequesterId).IsRequired();

        builder.Ignore(one => one.Reference);
        builder.Ignore(one => one.IsResolved);
        builder.Ignore(one => one.AsTheySeeIt);

        builder.HasIndex(one => one.Number).IsUnique().HasDatabaseName("IX_tickets_number");

        /*
         * The queue's own order: what is open, soonest promise first. It is the one query this
         * section runs on every page load, and the promise is the column somebody sorts on
         * rather than the date it arrived — which is the whole point of storing a target.
         */
        builder.HasIndex(one => new { one.Status, one.RespondBy })
            .HasDatabaseName("IX_tickets_queue");

        /*
         * Indexed without a foreign key, because the column points at two tables — an employee
         * or a client's contact — and a key can only choose one. The alternative is two
         * nullable columns of which exactly one is filled, which no constraint can express
         * either and which reads worse.
         */
        builder.HasIndex(one => one.RequesterId).HasDatabaseName("IX_tickets_requester");

        builder.HasOne<Client>()
            .WithMany()
            .HasForeignKey(one => one.ClientId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.OwnsMany(one => one.Messages, said =>
        {
            said.ToTable("ticket_messages");
            said.WithOwner().HasForeignKey("TicketId");

            said.HasKey(row => row.Id);

            said.Property(row => row.Audience).HasConversion<int>().IsRequired();
            said.Property(row => row.Text).HasMaxLength(10_000).IsRequired();
            said.Property(row => row.At).IsRequired();

            /*
             * Indexed on the audience as well as the ticket, because the one query in this
             * section that must never be wrong is what the requester may read — and a filter
             * applied in memory is one edit away from not being applied at all.
             */
            said.HasIndex("TicketId", nameof(TicketMessage.Audience))
                .HasDatabaseName("IX_ticket_messages_audience");
        });
    }
}
