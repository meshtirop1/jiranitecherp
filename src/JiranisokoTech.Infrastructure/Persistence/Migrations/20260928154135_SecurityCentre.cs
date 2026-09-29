using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiranisokoTech.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SecurityCentre : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "access_reviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReviewerId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_access_reviews", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "access_review_lines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Roles = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    UsesSecondFactor = table.Column<bool>(type: "boolean", nullable: false),
                    LastSignedInAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Kept = table.Column<bool>(type: "boolean", nullable: false),
                    AccessReviewId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_access_review_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_access_review_lines_access_reviews_AccessReviewId",
                        column: x => x.AccessReviewId,
                        principalTable: "access_reviews",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sign_in_records_At",
                table: "sign_in_records",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_access_review_lines_AccessReviewId",
                table: "access_review_lines",
                column: "AccessReviewId");

            migrationBuilder.CreateIndex(
                name: "IX_access_reviews_ReviewedAt",
                table: "access_reviews",
                column: "ReviewedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "access_review_lines");

            migrationBuilder.DropTable(
                name: "access_reviews");

            migrationBuilder.DropIndex(
                name: "IX_sign_in_records_At",
                table: "sign_in_records");
        }
    }
}
