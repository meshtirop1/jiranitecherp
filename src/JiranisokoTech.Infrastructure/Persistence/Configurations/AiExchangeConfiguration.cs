using JiranisokoTech.Infrastructure.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiranisokoTech.Infrastructure.Persistence.Configurations;

public sealed class AiExchangeConfiguration : IEntityTypeConfiguration<AiExchange>
{
    public void Configure(EntityTypeBuilder<AiExchange> builder)
    {
        builder.ToTable("ai_exchanges");

        builder.HasKey(one => one.Id);

        builder.Property(one => one.AskedBy).HasMaxLength(200);
        builder.Property(one => one.Feature).HasConversion<int>().IsRequired();
        builder.Property(one => one.Question).HasMaxLength(2000);
        builder.Property(one => one.Lookups).HasMaxLength(2000).IsRequired();
        builder.Property(one => one.Outcome).HasConversion<int>().IsRequired();
        builder.Property(one => one.Problem).HasMaxLength(500);
        builder.Property(one => one.Model).HasMaxLength(100).IsRequired();

        /*
         * The daily limit's question, asked before every use: how many has this account had since
         * midnight. Without the index that is a scan of the whole log on every question, and the
         * log only grows.
         */
        builder.HasIndex(one => new { one.AccountId, one.At });

        // The usage page reads the newest first, for everybody.
        builder.HasIndex(one => one.At);
    }
}
