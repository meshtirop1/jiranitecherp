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
         * What is owed right now, soonest first, and it is a different question from the index
         * above rather than a variation on it.
         *
         * The queue index is (Status, RespondBy), and every query that asks "who is late" filters
         * the status with an inequality — not resolved — which puts a range on the leading column
         * and makes the rest of the index unusable as a seek. So the two most frequent reads in
         * this section, the count beside the navigation link and the escalation sweep, were both
         * scanning the table.
         *
         * AnswerOwedBy is null for every ticket that owes nothing, which is most of them once a
         * desk has been running a while, and that is exactly what makes it a good leading column:
         * the rows this index has to hold are the rows somebody is waiting on.
         */
        builder.HasIndex(one => new { one.AnswerOwedBy, one.EscalatedAt })
            .HasDatabaseName("IX_tickets_owed");

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
             * On the ticket alone, and the audience column that used to be here is gone.
             *
             * The comment that justified it said the audience must not be filtered in memory —
             * and it is filtered in memory, by AsTheySeeIt, which reads the thread the aggregate
             * has already loaded. There is no query anywhere that filters on this column, so the
             * second half of that index earned nothing and its comment described the opposite of
             * what the code does, which is worse than no comment.
             *
             * Filtering in memory is the right arrangement here and worth saying plainly: a thread
             * is a handful of rows that the ticket page loads whole, and the audience decides how
             * each one is DRAWN as well as who may see it. Two queries for one list would be two
             * places to keep in step.
             */
            said.HasIndex("TicketId").HasDatabaseName("IX_ticket_messages_ticket");
        });
    }
}
