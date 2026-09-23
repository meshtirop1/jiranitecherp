using JiranisokoTech.Domain.Renewals;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiranisokoTech.Infrastructure.Persistence.Configurations;

/// <summary>
/// The ledger of notices already given.
/// </summary>
/// <remarks>
/// Its own file rather than an addition to an existing group, because it belongs to no
/// existing bounded context: it is read by the contract job, the qualification job and the
/// invoice job, and owned by none of them.
/// </remarks>
public sealed class ReminderConfiguration : IEntityTypeConfiguration<Reminder>
{
    public void Configure(EntityTypeBuilder<Reminder> builder)
    {
        builder.ToTable("reminders");

        builder.HasKey(one => one.Id);

        builder.Property(one => one.Kind).HasConversion<int>().IsRequired();
        builder.Property(one => one.Stage).HasConversion<int>().IsRequired();
        builder.Property(one => one.Subject).HasMaxLength(300).IsRequired();

        /*
         * The index that carries the whole point of the table.
         *
         * Both scheduled mail jobs declare IRecurringJob, whose contract says every job must
         * be safe to run twice and that running twice finds nothing the second time. Neither
         * was: each asked "what expires within N days" and mailed everybody about all of it
         * every morning, so a contract ending in forty-five days produced forty-five identical
         * emails to every department head. This is what the second run now collides with.
         *
         * Unique rather than merely indexed, because the check that reads it and the write
         * that follows are not one statement. Two schedulers — one in a container that is
         * being replaced by another — would both find nothing and both send, and the index is
         * the only thing that actually refuses the second.
         *
         * The deadline is in the key on purpose. A contract whose end date is extended has a
         * new deadline and genuinely should be warned about again; keying on subject and stage
         * alone would silence every future notice for anything warned about once.
         */
        builder.HasIndex(one => new
            {
                one.Kind,
                one.SubjectId,
                one.DeadlineOn,
                one.Stage,
            })
            .IsUnique()
            .HasDatabaseName("IX_reminders_notice_given");

        /*
         * Named explicitly, because the generated name for a four-column index on this table
         * runs past PostgreSQL's sixty-three character identifier limit, and a truncated name
         * is how two indexes silently become one.
         */
        builder.HasIndex(one => one.At);

        /*
         * No foreign key to the contract, the employee or the invoice, and that is deliberate
         * rather than an omission. This is a record that a notice was sent, and it has to
         * outlive whatever it was about — an invoice deleted or a contract removed must not
         * take with it the evidence that the firm gave warning. The subject is kept in words
         * for the same reason: so the row still reads after the thing it names has gone.
         */
    }
}
