using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Repository = JiranisokoTech.Domain.Engineering.Repository;

namespace JiranisokoTech.Infrastructure.Persistence.Configurations;

public sealed class ServiceConfiguration : IEntityTypeConfiguration<Service>
{
    public void Configure(EntityTypeBuilder<Service> builder)
    {
        builder.ToTable("services");

        builder.HasKey(one => one.Id);

        builder.Property(one => one.Name).HasMaxLength(200).IsRequired();
        builder.Property(one => one.Description).HasMaxLength(2_000).IsRequired();
        builder.Property(one => one.Matters).HasConversion<int>().IsRequired();
        builder.Property(one => one.Notes).HasMaxLength(2_000);

        builder.Ignore(one => one.IsLive);

        /*
         * Not a unique index, deliberately, although the service checks for a duplicate name.
         * Names here are typed by people and differ in case and spacing — "Despatch board" and
         * "despatch  board" — so an index would catch the exact repeat and miss the one that
         * actually happens. The check compares case-insensitively and the index would give a
         * worse message for a narrower set of mistakes.
         */
        builder.HasIndex(one => one.Name);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(one => one.OwnerId)
            .OnDelete(DeleteBehavior.SetNull);

        /*
         * SetNull, because a repository being disconnected is an administrative act and must not
         * take the catalogue entry with it. The service is still running either way.
         */
        builder.HasOne<Repository>()
            .WithMany()
            .HasForeignKey(one => one.RepositoryId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class ResourceConfiguration : IEntityTypeConfiguration<Resource>
{
    public void Configure(EntityTypeBuilder<Resource> builder)
    {
        builder.ToTable("resources");

        builder.HasKey(one => one.Id);

        builder.Property(one => one.Name).HasMaxLength(300).IsRequired();
        builder.Property(one => one.Kind).HasConversion<int>().IsRequired();
        builder.Property(one => one.Provider).HasMaxLength(200);
        builder.Property(one => one.Environment).HasConversion<int>().IsRequired();
        builder.Property(one => one.Address).HasMaxLength(500);
        builder.Property(one => one.Notes).HasMaxLength(2_000);

        builder.Ignore(one => one.IsLive);

        /*
         * The one query anything runs on its own: what expires soon. The reminder job reads it
         * every morning, so it is worth an index even though this table has tens of rows rather
         * than millions — the job is the only thing here that runs without somebody asking, and
         * a job that scans is a job that gets slower every year without anybody watching.
         */
        builder.HasIndex(one => one.ExpiresOn);

        builder.HasIndex(one => new { one.Environment, one.Kind })
            .HasDatabaseName("IX_resources_environment_kind");

        builder.HasIndex(one => one.ServiceId);

        /*
         * SetNull rather than Cascade. Retiring a service must not delete the server it ran on:
         * the machine is still there, still costing money, and is exactly the thing somebody is
         * trying to find when they ask what is still running for a product nobody sells.
         */
        builder.HasOne<Service>()
            .WithMany()
            .HasForeignKey(one => one.ServiceId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
