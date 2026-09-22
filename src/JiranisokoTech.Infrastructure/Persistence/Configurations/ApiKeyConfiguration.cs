using JiranisokoTech.Domain.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiranisokoTech.Infrastructure.Persistence.Configurations;

public sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> builder)
    {
        builder.ToTable("api_keys");

        builder.HasKey(key => key.Id);

        builder.Property(key => key.Name).HasMaxLength(120).IsRequired();
        builder.Property(key => key.Hash).HasMaxLength(64).IsRequired();
        builder.Property(key => key.Hint).HasMaxLength(8).IsRequired();
        builder.Property(key => key.RevokedReason).HasMaxLength(500);

        // Stored as one text column rather than a child table. Scopes are read
        // and written as a set, always together, never queried across keys —
        // which is the whole case for a join table and none of it applies.
        builder.Property(key => key.Scopes)
            .HasConversion(
                scopes => string.Join(' ', scopes),
                text => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(),
                new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<IReadOnlyList<string>>(
                    (left, right) => left!.SequenceEqual(right!),
                    scopes => scopes.Aggregate(0, (hash, scope) => HashCode.Combine(hash, scope.GetHashCode())),
                    scopes => scopes.ToList()))
            .HasMaxLength(2000)
            .IsRequired();

        builder.Ignore(key => key.IsLive);

        // Every request looks a key up by this and by nothing else.
        builder.HasIndex(key => key.Hash).IsUnique();
    }
}
