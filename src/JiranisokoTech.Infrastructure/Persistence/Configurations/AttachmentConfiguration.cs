using JiranisokoTech.Domain.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiranisokoTech.Infrastructure.Persistence.Configurations;

public sealed class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> builder)
    {
        builder.ToTable("attachments");

        builder.HasKey(one => one.Id);

        builder.Property(one => one.Kind).HasConversion<int>().IsRequired();
        builder.Property(one => one.FileName).HasMaxLength(300).IsRequired();
        builder.Property(one => one.StoredName).HasMaxLength(100).IsRequired();
        builder.Property(one => one.Note).HasMaxLength(500);

        builder.Property(one => one.Tags).HasMaxLength(500);

        builder.Ignore(one => one.Size);
        builder.Ignore(one => one.IsCurrent);

        /*
         * Which version is current, per thing. Every list of documents asks for the current
         * ones, and without this the query reads every superseded version to discard it.
         */
        builder.HasIndex(one => new { one.Kind, one.OwnerId, one.SupersededAt });

        // Every read is "what is attached to this one thing", so that is the
        // index. Kind first because it narrows hardest.
        builder.HasIndex(one => new { one.Kind, one.OwnerId });

        // The name on disk is unique by construction; the index says so, and
        // would catch a generator that ever stopped being.
        builder.HasIndex(one => one.StoredName).IsUnique();
    }
}
