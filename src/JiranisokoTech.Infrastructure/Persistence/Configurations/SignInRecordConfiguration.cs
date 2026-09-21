using JiranisokoTech.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiranisokoTech.Infrastructure.Persistence.Configurations;

public sealed class SignInRecordConfiguration : IEntityTypeConfiguration<SignInRecord>
{
    public void Configure(EntityTypeBuilder<SignInRecord> builder)
    {
        builder.ToTable("sign_in_records");

        builder.HasKey(record => record.Id);

        builder.Property(record => record.Email).HasMaxLength(255).IsRequired();
        builder.Property(record => record.IpAddress).HasMaxLength(45);
        builder.Property(record => record.UserAgent).HasMaxLength(400);
        builder.Property(record => record.At).IsRequired();

        // Stored as its number rather than its name. An enum written as text is
        // a value somebody renames in code and orphans in the database.
        builder.Property(record => record.Outcome).HasConversion<int>();

        // The two readings: one person's own history, newest first, and
        // everything from one address when somebody is looking for a pattern.
        builder.HasIndex(record => new { record.UserId, record.At });
        builder.HasIndex(record => new { record.IpAddress, record.At });

        // No foreign key to the user, deliberately. A record can name an
        // address that matches no account at all, and those are the rows worth
        // keeping when somebody is working through a list.
    }
}
