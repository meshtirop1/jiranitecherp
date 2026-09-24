using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiranisokoTech.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FeatureFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "flags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RetiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_flags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "flag_changes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Environment = table.Column<int>(type: "integer", nullable: false),
                    On = table.Column<bool>(type: "boolean", nullable: false),
                    Why = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ById = table.Column<Guid>(type: "uuid", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FlagId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_flag_changes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_flag_changes_flags_FlagId",
                        column: x => x.FlagId,
                        principalTable: "flags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "flag_settings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Environment = table.Column<int>(type: "integer", nullable: false),
                    On = table.Column<bool>(type: "boolean", nullable: false),
                    FlagId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_flag_settings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_flag_settings_flags_FlagId",
                        column: x => x.FlagId,
                        principalTable: "flags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_flag_changes_At",
                table: "flag_changes",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_flag_changes_FlagId",
                table: "flag_changes",
                column: "FlagId");

            migrationBuilder.CreateIndex(
                name: "IX_flag_settings_FlagId_Environment",
                table: "flag_settings",
                columns: new[] { "FlagId", "Environment" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_flags_Key",
                table: "flags",
                column: "Key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "flag_changes");

            migrationBuilder.DropTable(
                name: "flag_settings");

            migrationBuilder.DropTable(
                name: "flags");
        }
    }
}
