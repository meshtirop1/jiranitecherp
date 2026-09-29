using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiranisokoTech.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KnowledgeBase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "articles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Body = table.Column<string>(type: "character varying(40000)", maxLength: 40000, nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Labels = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    WrittenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastCheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastCheckedById = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewBy = table.Column<DateOnly>(type: "date", nullable: true),
                    RetiredBecause = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RetiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_articles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_articles_employees_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "article_revisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Body = table.Column<string>(type: "character varying(40000)", maxLength: 40000, nullable: false),
                    ByEmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ArticleId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_article_revisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_article_revisions_articles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "articles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_article_revisions_when",
                table: "article_revisions",
                columns: new[] { "ArticleId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_articles_key",
                table: "articles",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_articles_OwnerId",
                table: "articles",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_articles_standing",
                table: "articles",
                columns: new[] { "State", "ReviewBy" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "article_revisions");

            migrationBuilder.DropTable(
                name: "articles");
        }
    }
}
