using JiranisokoTech.Domain.Common;
using JiranisokoTech.Domain.Vendors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiranisokoTech.Infrastructure.Persistence.Configurations;

public sealed class VendorConfiguration : IEntityTypeConfiguration<Vendor>
{
    public void Configure(EntityTypeBuilder<Vendor> builder)
    {
        builder.ToTable("vendors");

        builder.HasKey(one => one.Id);

        builder.Property(one => one.Name).HasMaxLength(200).IsRequired();
        builder.Property(one => one.Code).HasMaxLength(Slug.MaximumLength).IsRequired();
        builder.Property(one => one.Supplies).HasMaxLength(300);
        builder.Property(one => one.Address).HasMaxLength(500);
        builder.Property(one => one.TaxPin).HasMaxLength(20);
        builder.Property(one => one.Notes).HasMaxLength(2_000);
        builder.Property(one => one.Status).HasConversion<int>().IsRequired();

        builder.Ignore(one => one.Handle);
        builder.Ignore(one => one.IsCurrent);

        /*
         * Unique on the code rather than on the name, and that is the whole reason the code
         * exists. A unique index on a name is case-sensitive, so "Safaricom" and "safaricom"
         * would be two suppliers — the exact fault Asset.Tag upper-cases to avoid — and this
         * section fails the moment the firm has two rows for one company. A slug is reduced on
         * the way in, so the constraint catches what it looks like it catches.
         */
        builder.HasIndex(one => one.Code).IsUnique();

        builder.HasIndex(one => one.Status);
    }
}

public sealed class VendorContactConfiguration : IEntityTypeConfiguration<VendorContact>
{
    public void Configure(EntityTypeBuilder<VendorContact> builder)
    {
        builder.ToTable("vendor_contacts");

        builder.HasKey(one => one.Id);

        builder.Property(one => one.Name).HasMaxLength(200).IsRequired();
        builder.Property(one => one.JobTitle).HasMaxLength(200);
        builder.Property(one => one.Email).HasMaxLength(320);
        builder.Property(one => one.Phone).HasMaxLength(50);

        builder.Ignore(one => one.IsHere);

        /*
         * The question this table is asked: who is at this supplier, and who has left. The same
         * index the client contact book carries, for the same read.
         */
        builder.HasIndex(one => new { one.VendorId, one.GoneAt });

        /*
         * Cascade, as a client's contacts do. A contact exists only as somebody at that company;
         * with the supplier gone the row describes nobody, which is different from a leaver's row
         * — that still explains an email in the archive.
         *
         * In practice nothing deletes a vendor: Former is the end state, and the aggregate has no
         * delete. The cascade is what keeps the schema honest rather than something anybody
         * expects to fire.
         */
        builder.HasOne<Vendor>()
            .WithMany()
            .HasForeignKey(one => one.VendorId)
            .OnDelete(DeleteBehavior.Cascade);

        /*
         * No unique index enforcing one main contact per vendor. It would have to be a partial
         * index over (VendorId) where IsMain, which EF cannot express portably, and the rule is
         * richer than uniqueness anyway — promoting clears the previous holder, and when the
         * holder leaves the longest-serving of whoever remains inherits. That lives in
         * WhoToCallFirst, shared with the client contact book.
         */
    }
}
