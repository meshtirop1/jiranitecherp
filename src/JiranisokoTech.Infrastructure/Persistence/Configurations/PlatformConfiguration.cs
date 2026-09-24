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

public sealed class FlagConfiguration : IEntityTypeConfiguration<Flag>
{
    public void Configure(EntityTypeBuilder<Flag> builder)
    {
        builder.ToTable("flags");

        builder.HasKey(one => one.Id);

        builder.Property(one => one.Key).HasMaxLength(120).IsRequired();
        builder.Property(one => one.Description).HasMaxLength(2_000).IsRequired();

        builder.Ignore(one => one.IsLive);

        /*
         * Unique, and it is the one index here that an application depends on rather than a
         * screen: the key is what source code somewhere else asks for, and two rows answering
         * to it means an application gets whichever the database returned first. The key is
         * lower-cased on the way in for the same reason.
         */
        builder.HasIndex(one => one.Key).IsUnique();

        builder.OwnsMany(one => one.Settings, setting =>
        {
            setting.ToTable("flag_settings");
            setting.WithOwner().HasForeignKey("FlagId");

            // The key is a GUIDv7 the domain assigned — see the note on pull request reviews.
            setting.HasKey(one => one.Id);

            setting.Property(one => one.Environment).HasConversion<int>().IsRequired();

            /*
             * One row per environment per flag. Two would let a flag be both on and off in
             * production, and the answer an application got would depend on row order.
             */
            setting.HasIndex("FlagId", nameof(FlagSetting.Environment)).IsUnique();
        });

        builder.OwnsMany(one => one.Changes, change =>
        {
            change.ToTable("flag_changes");
            change.WithOwner().HasForeignKey("FlagId");

            change.HasKey(one => one.Id);

            change.Property(one => one.Environment).HasConversion<int>().IsRequired();
            change.Property(one => one.Why).HasMaxLength(500).IsRequired();

            /*
             * Read by an incident asking what changed in a window across every flag, not by one
             * flag's own page — so the index is on the time rather than on the owner.
             */
            change.HasIndex(nameof(FlagChange.At));
        });
    }
}
