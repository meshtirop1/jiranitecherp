using JiranisokoTech.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiranisokoTech.Infrastructure.Persistence.Configurations;

public sealed class RecoveryAskConfiguration : IEntityTypeConfiguration<RecoveryAsk>
{
    public void Configure(EntityTypeBuilder<RecoveryAsk> builder)
    {
        builder.ToTable("recovery_asks");

        builder.HasKey(ask => ask.Id);

        builder.Property(ask => ask.Email).HasMaxLength(255).IsRequired();
        builder.Property(ask => ask.Outcome).HasConversion<int>().IsRequired();
        builder.Property(ask => ask.IpAddress).HasMaxLength(45);
        builder.Property(ask => ask.UserAgent).HasMaxLength(400);

        /*
         * The address and the time, which is the only question this table is asked: how many
         * links have been sent to this address in the last hour. A plain composite index
         * rather than a filtered one, because the test databases are SQLite and HasFilter
         * takes raw provider SQL — a PostgreSQL-shaped filter here would take the whole suite
         * down with it.
         */
        builder.HasIndex(ask => new { ask.Email, ask.At });

        /*
         * No foreign key to the account, on purpose and for a sharper reason than
         * SignInRecord has. A request about an address that matches no account has no account
         * to point at, and those rows are the ones worth keeping — somebody working through a
         * list of addresses leaves a trail of exactly them.
         */
    }
}
