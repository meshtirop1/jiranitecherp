using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiranisokoTech.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Occasions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "occasions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    On = table.Column<DateOnly>(type: "date", nullable: false),
                    Until = table.Column<DateOnly>(type: "date", nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_occasions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_occasions_On",
                table: "occasions",
                column: "On");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "occasions");
        }
    }
}
