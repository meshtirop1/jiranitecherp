using JiranisokoTech.Domain.Knowledge;
using JiranisokoTech.Domain.People;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiranisokoTech.Infrastructure.Persistence.Configurations;

public sealed class ArticleConfiguration : IEntityTypeConfiguration<Article>
{
    public void Configure(EntityTypeBuilder<Article> builder)
    {
        builder.ToTable("articles");

        builder.HasKey(one => one.Id);

        builder.Property(one => one.Key).HasMaxLength(200).IsRequired();
        builder.Property(one => one.Title).HasMaxLength(200).IsRequired();
        builder.Property(one => one.Summary).HasMaxLength(500).IsRequired();
        builder.Property(one => one.Body).HasMaxLength(Article.LongestBody).IsRequired();
        builder.Property(one => one.Labels).HasMaxLength(400);
        builder.Property(one => one.State).HasConversion<int>().IsRequired();
        builder.Property(one => one.RetiredBecause).HasMaxLength(1_000);

        builder.Ignore(one => one.IsPublished);

        /*
         * The address, unique, with no nullable column in it. That matters more here than it
         * looks: the key is what a pasted link resolves against, so two rows answering to one
         * address would mean a link opening a different article depending on which row the
         * query happened to reach first.
         */
        builder.HasIndex(one => one.Key).IsUnique().HasDatabaseName("IX_articles_key");

        /*
         * The list's own query, and the stale list's. Both ask what is published and by when
         * somebody owes it a look — the review date is the column this whole section turns on,
         * and a scan to find what has gone stale is a scan that gets slower exactly as the
         * knowledge base becomes worth having.
         */
        builder.HasIndex(one => new { one.State, one.ReviewBy })
            .HasDatabaseName("IX_articles_standing");

        /*
         * Restrict on the owner, like an announcement's author. An article is answerable for by
         * a named person and the row must not lose that name; nothing in this system deletes an
         * employee anyway, because people leave and leaving keeps the row.
         */
        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(one => one.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.OwnsMany(one => one.Revisions, version =>
        {
            version.ToTable("article_revisions");
            version.WithOwner().HasForeignKey("ArticleId");

            version.HasKey(row => row.Id);

            version.Property(row => row.Body).HasMaxLength(Article.LongestBody).IsRequired();
            version.Property(row => row.ByEmployeeId).IsRequired();
            version.Property(row => row.At).IsRequired();
            version.Property(row => row.Note).HasMaxLength(500);

            /*
             * No unique index, unlike an acknowledgement. Publishing the same article twice in
             * one day is ordinary — somebody publishes, spots a mistake, fixes it and publishes
             * again — and both are versions the firm stood behind for a while, which is exactly
             * what this table is for.
             */
            version.HasIndex("ArticleId", nameof(ArticleRevision.At))
                .HasDatabaseName("IX_article_revisions_when");
        });
    }
}
