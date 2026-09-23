using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiranisokoTech.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DocumentVersionsAndTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SupersededAt",
                table: "attachments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupersededById",
                table: "attachments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tags",
                table: "attachments",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_attachments_Kind_OwnerId_SupersededAt",
                table: "attachments",
                columns: new[] { "Kind", "OwnerId", "SupersededAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_attachments_Kind_OwnerId_SupersededAt",
                table: "attachments");

            migrationBuilder.DropColumn(
                name: "SupersededAt",
                table: "attachments");

            migrationBuilder.DropColumn(
                name: "SupersededById",
                table: "attachments");

            migrationBuilder.DropColumn(
                name: "Tags",
                table: "attachments");
        }
    }
}
