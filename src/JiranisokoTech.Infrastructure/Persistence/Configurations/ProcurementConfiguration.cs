using JiranisokoTech.Domain.Money;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Procurement;
using JiranisokoTech.Domain.Vendors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiranisokoTech.Infrastructure.Persistence.Configurations;

public sealed class PurchaseRequestConfiguration : IEntityTypeConfiguration<PurchaseRequest>
{
    public void Configure(EntityTypeBuilder<PurchaseRequest> builder)
    {
        builder.ToTable("purchase_requests");

        builder.HasKey(one => one.Id);

        builder.Property(one => one.Reference).HasMaxLength(30).IsRequired();
        builder.Property(one => one.Justification).HasMaxLength(4_000).IsRequired();
        builder.Property(one => one.State).HasConversion<int>().IsRequired();
        builder.Property(one => one.Outcome).HasMaxLength(1_000);

        builder.Ignore(one => one.IsDraft);
        builder.Ignore(one => one.IsApproved);
        builder.Ignore(one => one.Estimate);

        // Unique, because the reference is what somebody quotes in an email about it.
        builder.HasIndex(one => one.Reference).IsUnique();

        builder.HasIndex(one => new { one.State, one.RaisedAt });

        /*
         * Restrict on the person who asked. A request is a decision the firm took about money,
         * and deleting a staff record must not take the argument for spending it with them.
         */
        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(one => one.RaisedById)
            .OnDelete(DeleteBehavior.Restrict);

        builder.OwnsMany(one => one.Lines, line =>
        {
            line.ToTable("purchase_request_lines");
            line.WithOwner().HasForeignKey("PurchaseRequestId");

            line.HasKey(row => row.Id);

            line.Property(row => row.Description).HasMaxLength(300).IsRequired();
            line.Property(row => row.Currency).HasMaxLength(3).IsRequired();

            line.Ignore(row => row.Unit);
            line.Ignore(row => row.Estimate);
        });
    }
}

public sealed class PurchaseOrderConfiguration : IEntityTypeConfiguration<PurchaseOrder>
{
    public void Configure(EntityTypeBuilder<PurchaseOrder> builder)
    {
        builder.ToTable("purchase_orders");

        builder.HasKey(one => one.Id);

        builder.Property(one => one.Number).HasMaxLength(30).IsRequired();
        builder.Property(one => one.State).HasConversion<int>().IsRequired();
        builder.Property(one => one.Notes).HasMaxLength(2_000);
        builder.Property(one => one.Outcome).HasMaxLength(1_000);

        builder.Ignore(one => one.IsDraft);
        builder.Ignore(one => one.IsPlaced);
        builder.Ignore(one => one.Currency);
        builder.Ignore(one => one.Total);
        builder.Ignore(one => one.Paid);
        builder.Ignore(one => one.EverythingSettled);

        builder.HasIndex(one => one.Number).IsUnique();
        builder.HasIndex(one => new { one.State, one.RaisedAt });
        builder.HasIndex(one => one.VendorId);

        /*
         * Restrict on the supplier, matching the agreement link. A supplier is never deleted —
         * Former is the end state — so if anything ever tries, it must fail loudly rather than
         * quietly cut an order adrift from who it was placed with.
         */
        builder.HasOne<Vendor>()
            .WithMany()
            .HasForeignKey(one => one.VendorId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(one => one.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(one => one.RaisedById)
            .OnDelete(DeleteBehavior.Restrict);

        builder.OwnsMany(one => one.Lines, line =>
        {
            line.ToTable("purchase_order_lines");
            line.WithOwner().HasForeignKey("PurchaseOrderId");

            line.HasKey(row => row.Id);

            line.Property(row => row.Description).HasMaxLength(300).IsRequired();
            line.Property(row => row.Currency).HasMaxLength(3).IsRequired();

            line.Ignore(row => row.Unit);
            line.Ignore(row => row.Amount);

            /*
             * Indexed, because "has anything been ordered against this request" is the query the
             * purchasing screen opens with and it comes in through this column.
             */
            line.HasIndex(row => row.RequestLineId);
        });

        builder.OwnsMany(one => one.Receipts, receipt =>
        {
            receipt.ToTable("goods_receipts");
            receipt.WithOwner().HasForeignKey("PurchaseOrderId");

            receipt.HasKey(row => row.Id);

            receipt.Property(row => row.Condition).HasConversion<int>().IsRequired();
            receipt.Property(row => row.Note).HasMaxLength(1_000);

            receipt.HasIndex(row => row.OrderLineId);
        });

        builder.OwnsMany(one => one.Payments, payment =>
        {
            payment.ToTable("vendor_payments");
            payment.WithOwner().HasForeignKey("PurchaseOrderId");

            payment.HasKey(row => row.Id);

            payment.Property(row => row.Currency).HasMaxLength(3).IsRequired();
            payment.Property(row => row.Reference).HasMaxLength(100);

            payment.Ignore(row => row.Amount);
        });
    }
}
