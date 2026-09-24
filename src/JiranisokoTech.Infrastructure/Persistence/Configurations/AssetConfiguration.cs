using JiranisokoTech.Domain.Assets;
using JiranisokoTech.Domain.People;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiranisokoTech.Infrastructure.Persistence.Configurations;

public sealed class AssetConfiguration : IEntityTypeConfiguration<Asset>
{
    public void Configure(EntityTypeBuilder<Asset> builder)
    {
        builder.ToTable("assets");

        builder.HasKey(one => one.Id);

        builder.Property(one => one.Tag).HasMaxLength(40).IsRequired();
        builder.Property(one => one.Kind).HasConversion<int>().IsRequired();
        builder.Property(one => one.Description).HasMaxLength(300).IsRequired();
        builder.Property(one => one.SerialNumber).HasMaxLength(100);
        builder.Property(one => one.CostCurrency).HasMaxLength(3);
        builder.Property(one => one.Status).HasConversion<int>().IsRequired();
        builder.Property(one => one.Notes).HasMaxLength(2_000);

        builder.Ignore(one => one.Cost);
        builder.Ignore(one => one.IsOut);
        builder.Ignore(one => one.IsLive);

        /*
         * The firm's own label, and no two things share one. It is upper-cased on the way in
         * because this index is case-sensitive, so JD-014 typed as jd-014 would otherwise be a
         * second row for the same laptop — which is the exact failure a register exists to
         * prevent.
         */
        builder.HasIndex(one => one.Tag).IsUnique();

        /*
         * "What is this person holding" is asked by the joiner's checklist and by every leaver's
         * screen, and it is the query the whole of section 15 is for.
         */
        builder.HasIndex(one => new { one.HeldById, one.Status })
            .HasDatabaseName("IX_assets_held_by_status");

        builder.HasIndex(one => one.Status);

        /*
         * SetNull rather than Cascade. Deleting somebody from the staff list must not delete the
         * laptop they were holding — the machine is the firm's, and the row is how anybody finds
         * out it is unaccounted for.
         */
        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(one => one.HeldById)
            .OnDelete(DeleteBehavior.SetNull);

        builder.OwnsMany(one => one.Movements, movement =>
        {
            movement.ToTable("asset_movements");
            movement.WithOwner().HasForeignKey("AssetId");

            // The key is a GUIDv7 the domain assigned — see the note on pull request reviews.
            movement.HasKey(one => one.Id);

            movement.Property(one => one.To).HasConversion<int>().IsRequired();
            movement.Property(one => one.What).HasMaxLength(500).IsRequired();

            movement.HasIndex("AssetId", nameof(AssetMovement.On));
        });
    }
}
