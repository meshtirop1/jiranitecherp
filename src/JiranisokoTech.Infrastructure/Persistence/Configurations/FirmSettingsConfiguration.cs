using JiranisokoTech.Domain.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiranisokoTech.Infrastructure.Persistence.Configurations;

public sealed class FirmSettingsConfiguration : IEntityTypeConfiguration<FirmSettings>
{
    public void Configure(EntityTypeBuilder<FirmSettings> builder)
    {
        builder.ToTable("firm_settings");

        builder.HasKey(settings => settings.Id);

        builder.Property(settings => settings.TradingName).HasMaxLength(200).IsRequired();
        builder.Property(settings => settings.LegalName).HasMaxLength(200).IsRequired();
        builder.Property(settings => settings.TaxPin).HasMaxLength(30);
        builder.Property(settings => settings.AddressLine).HasMaxLength(300);
        builder.Property(settings => settings.Town).HasMaxLength(100);
        builder.Property(settings => settings.PostalCode).HasMaxLength(20);
        builder.Property(settings => settings.Country).HasMaxLength(100).IsRequired();
        builder.Property(settings => settings.Telephone).HasMaxLength(40);
        builder.Property(settings => settings.Email).HasMaxLength(320);
        builder.Property(settings => settings.Website).HasMaxLength(300);
        builder.Property(settings => settings.PaymentInstructions).HasMaxLength(2000);
        builder.Property(settings => settings.InvoicePrefix).HasMaxLength(8).IsRequired();
        builder.Property(settings => settings.Currency).HasMaxLength(3).IsRequired();

        builder.Ignore(settings => settings.IsReadyToInvoice);
    }
}
