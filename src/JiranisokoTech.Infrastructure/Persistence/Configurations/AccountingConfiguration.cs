using JiranisokoTech.Domain.Money;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiranisokoTech.Infrastructure.Persistence.Configurations;

public sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.ToTable("accounts");

        builder.HasKey(account => account.Id);

        builder.Property(account => account.Code)
            .HasMaxLength(Account.MaximumCodeLength)
            .IsRequired();

        builder.Property(account => account.Name).HasMaxLength(200).IsRequired();
        builder.Property(account => account.Kind).HasConversion<int>().IsRequired();

        builder.Ignore(account => account.IsOpen);

        /*
         * One account per code, and this is what holds when two people add 4200 at the same
         * moment. Two rows with one code would mean a report that split one account's money
         * across two lines, and nothing about either line would look wrong — which is the
         * quietest kind of wrong a figure about money can be.
         */
        builder.HasIndex(account => account.Code).IsUnique();

        // The chart is read whole, grouped by side, on every report and every picker.
        builder.HasIndex(account => new { account.Kind, account.Code });
    }
}

public sealed class RecurringExpenseConfiguration : IEntityTypeConfiguration<RecurringExpense>
{
    public void Configure(EntityTypeBuilder<RecurringExpense> builder)
    {
        builder.ToTable("recurring_expenses");

        builder.HasKey(one => one.Id);

        builder.Property(one => one.Description).HasMaxLength(300).IsRequired();
        builder.Property(one => one.Payee).HasMaxLength(200).IsRequired();
        builder.Property(one => one.Currency).HasMaxLength(3).IsRequired();
        builder.Property(one => one.Every).HasConversion<int>().IsRequired();
        builder.Property(one => one.Status).HasConversion<int>().IsRequired();

        /*
         * Money as its minor units beside a three-letter code, never a decimal, and the
         * Money-typed property ignored. The same shape as every other amount in this schema:
         * a report can sum the column in SQL while the code still works with Money.
         */
        builder.Ignore(one => one.Amount);
        builder.Ignore(one => one.IsActive);
        builder.Ignore(one => one.Charges);

        // The job's own question: what is active, and what falls due next.
        builder.HasIndex(one => new { one.Status, one.StartsOn });

        /*
         * Restricted rather than cascading or nulling. An account with standing costs coded to
         * it cannot be removed without either destroying the costs or moving them somewhere
         * nobody chose — and the refusal is the conversation that should happen instead.
         */
        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(one => one.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.OwnsMany(one => one.Charges, charge =>
        {
            charge.ToTable("recurring_charges");
            charge.WithOwner().HasForeignKey("RecurringExpenseId");

            // The key is a GUIDv7 the domain assigned. AppDbContext's
            // OwnedKeysComeFromTheDomain is what stops EF taking that for an existing row
            // and silently never inserting it.
            charge.HasKey(one => one.Id);

            charge.Property(one => one.Currency).HasMaxLength(3).IsRequired();

            charge.Ignore(one => one.Amount);
            charge.Ignore(one => one.IsSettled);

            /*
             * What the report sums and what the job checks: the charges of one cost, by the
             * date they fell due. Not unique on the date, deliberately — the aggregate refuses
             * a duplicate before it gets here, and a unique index would turn a race between
             * two scheduler instances into an exception on a background job rather than a
             * second charge nobody raised.
             */
            charge.HasIndex(one => one.DueOn);
        });
    }
}
