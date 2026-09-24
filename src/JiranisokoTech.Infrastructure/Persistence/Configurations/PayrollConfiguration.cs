using JiranisokoTech.Domain.Payroll;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiranisokoTech.Infrastructure.Persistence.Configurations;

public sealed class PayRunConfiguration : IEntityTypeConfiguration<PayRun>
{
    public void Configure(EntityTypeBuilder<PayRun> builder)
    {
        builder.ToTable("pay_runs");

        builder.HasKey(run => run.Id);

        builder.Property(run => run.Currency).HasMaxLength(3).IsRequired();
        builder.Property(run => run.Status).HasConversion<int>().IsRequired();
        builder.Property(run => run.Outcome).HasMaxLength(500);

        builder.Ignore(run => run.Gross);
        builder.Ignore(run => run.Net);
        builder.Ignore(run => run.Cost);
        builder.Ignore(run => run.IsOpen);

        /*
         * The backing list, not the property. Payslips returns a copy so that EF is never handed
         * the real list — hand it that and it treats added rows as updates, and then nothing is
         * saved and nothing complains. This names the field instead.
         */
        builder.Metadata
            .FindNavigation(nameof(PayRun.Payslips))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(run => run.Payslips)
            .WithOne()
            .HasForeignKey(slip => slip.PayRunId)
            .OnDelete(DeleteBehavior.Cascade);

        /*
         * One run per period, enforced by the database rather than by a check somebody has to
         * remember. Two runs for March is a doubled cost in the accounts and two payslips per
         * person, and nothing on any screen would look wrong.
         *
         * Abandoned runs are excluded, because abandoning one exists precisely so the period can
         * be drafted again — a filtered index is what lets both be true at once.
         */
        builder.HasIndex(run => new { run.PeriodStart, run.PeriodEnd })
            .IsUnique()
            .HasFilter("\"Status\" <> 4")
            .HasDatabaseName("IX_pay_runs_one_per_period");
    }
}

public sealed class PayslipConfiguration : IEntityTypeConfiguration<Payslip>
{
    public void Configure(EntityTypeBuilder<Payslip> builder)
    {
        builder.ToTable("payslips");

        builder.HasKey(slip => slip.Id);

        builder.Property(slip => slip.Currency).HasMaxLength(3).IsRequired();
        builder.Property(slip => slip.GrossMinorUnits).IsRequired();

        builder.Ignore(slip => slip.Gross);
        builder.Ignore(slip => slip.Net);
        builder.Ignore(slip => slip.Deducted);
        builder.Ignore(slip => slip.EmployerPays);
        builder.Ignore(slip => slip.Cost);

        /*
         * One payslip per person per run. The aggregate refuses a second one, and this is what
         * holds when two people press the draft button at the same moment.
         */
        builder.HasIndex(slip => new { slip.PayRunId, slip.EmployeeId })
            .IsUnique()
            .HasDatabaseName("IX_payslips_one_per_person_per_run");

        // What somebody is owed, read by the person themselves.
        builder.HasIndex(slip => slip.EmployeeId);

        builder.OwnsMany(slip => slip.Lines, line =>
        {
            line.ToTable("payslip_lines");
            line.WithOwner().HasForeignKey("PayslipId");
            line.Property<int>("Id");
            line.HasKey("Id");

            line.Property(one => one.Name).HasMaxLength(100).IsRequired();
            line.Property(one => one.Kind).HasConversion<int>().IsRequired();

            /*
             * The arithmetic that produced the figure, kept beside it. A payslip has to be
             * defensible years after everybody has forgotten how it was worked out, and "PAYE
             * 36,384.35" answers nothing on its own.
             */
            line.Property(one => one.Basis).HasMaxLength(300);
        });

        builder.Navigation(slip => slip.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class StatutoryRatesConfiguration : IEntityTypeConfiguration<StatutoryRates>
{
    public void Configure(EntityTypeBuilder<StatutoryRates> builder)
    {
        builder.ToTable("statutory_rates");

        builder.HasKey(rates => rates.Id);

        builder.Property(rates => rates.Currency).HasMaxLength(3).IsRequired();
        builder.Property(rates => rates.Source).HasMaxLength(200);

        builder.Property(rates => rates.PensionEmployeeRate).HasPrecision(6, 5);
        builder.Property(rates => rates.PensionEmployerRate).HasPrecision(6, 5);
        builder.Property(rates => rates.HealthRate).HasPrecision(6, 5);
        builder.Property(rates => rates.HousingRate).HasPrecision(6, 5);

        builder.Ignore(rates => rates.Bands);

        /*
         * One set per date. A second set commencing the same day is two answers to "what were
         * the rates in March", and the payroll would take whichever the query returned first.
         */
        builder.HasIndex(rates => rates.InForceFrom)
            .IsUnique()
            .HasDatabaseName("IX_statutory_rates_in_force_from");

        builder.OwnsMany<TaxBand>("_bands", band =>
        {
            band.ToTable("tax_bands");
            band.WithOwner().HasForeignKey("StatutoryRatesId");
            band.Property<int>("Id");
            band.HasKey("Id");

            band.Property(one => one.Rate).HasPrecision(6, 5);
        });
    }
}
