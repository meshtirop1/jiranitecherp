using System.Text.Json;
using JiranisokoTech.Domain.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace JiranisokoTech.Infrastructure.Persistence.Configurations;

public sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.ToTable("audit_entries");

        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.Action).HasMaxLength(100).IsRequired();
        builder.Property(entry => entry.SubjectType).HasMaxLength(100).IsRequired();
        builder.Property(entry => entry.ActorName).HasMaxLength(200);
        builder.Property(entry => entry.Reason).HasMaxLength(2000);
        builder.Property(entry => entry.OccurredAt).IsRequired();

        /*
         * Before and after are stored as JSON text.
         *
         * Deliberately text and not a provider's native JSON type. §41 warns
         * against unstructured JSON for core relational data, and this is the
         * exception that proves the rule: these are not queried by field, they
         * are read whole by a human looking at one entry. Making them jsonb
         * would buy query operators nobody will use, at the cost of a schema
         * that only builds on PostgreSQL — and the test suite runs on SQLite.
         */
        var comparer = new ValueComparer<IReadOnlyDictionary<string, string?>?>(
            (left, right) => Serialize(left) == Serialize(right),
            // Null is a real state here — an insert has no 'before'. Hashing it
            // must not throw, so it hashes to zero rather than dereferencing null.
            dictionary => (Serialize(dictionary) ?? string.Empty).GetHashCode(),
            dictionary => dictionary);

        builder.Property(entry => entry.Before)
            .HasConversion(Converter)
            .Metadata.SetValueComparer(comparer);

        builder.Property(entry => entry.After)
            .HasConversion(Converter)
            .Metadata.SetValueComparer(comparer);

        /*
         * The two questions this table is asked, and one it is asked constantly.
         *
         * "What happened to this record" reads by subject; "what happened
         * lately", which is how the trail is usually opened, reads by time
         * descending. Without the second, every visit to an audit screen scans
         * the largest table in the database.
         */
        builder.HasIndex(entry => new { entry.SubjectType, entry.SubjectId });
        builder.HasIndex(entry => entry.OccurredAt).IsDescending();
        builder.HasIndex(entry => entry.ActorId);
        builder.HasIndex(entry => entry.Action);
    }

    private static readonly ValueConverter<IReadOnlyDictionary<string, string?>?, string?> Converter =
        new(
            dictionary => Serialize(dictionary),
            json => Deserialize(json));

    private static string? Serialize(IReadOnlyDictionary<string, string?>? dictionary) =>
        dictionary is null ? null : JsonSerializer.Serialize(dictionary, AppDbContext.JsonOptions);

    private static IReadOnlyDictionary<string, string?>? Deserialize(string? json) =>
        string.IsNullOrEmpty(json)
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, string?>>(json, AppDbContext.JsonOptions);
}
