using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiranisokoTech.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Offboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Education",
                table: "candidates",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExpectedSalaryCurrency",
                table: "candidates",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ExpectedSalaryMinorUnits",
                table: "candidates",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GitHub",
                table: "candidates",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LinkedIn",
                table: "candidates",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Portfolio",
                table: "candidates",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Skills",
                table: "candidates",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "YearsOfExperience",
                table: "candidates",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "offboardings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeavingOn = table.Column<DateOnly>(type: "date", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AccessRemovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AccessRemovedById = table.Column<Guid>(type: "uuid", nullable: true),
                    ExitInterviewAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExitInterviewNotes = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_offboardings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_offboardings_employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "lent_assets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Identifier = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ReturnedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Condition = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    OffboardingId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lent_assets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_lent_assets_offboardings_OffboardingId",
                        column: x => x.OffboardingId,
                        principalTable: "offboardings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_lent_assets_OffboardingId",
                table: "lent_assets",
                column: "OffboardingId");

            migrationBuilder.CreateIndex(
                name: "IX_lent_assets_ReturnedOn",
                table: "lent_assets",
                column: "ReturnedOn");

            migrationBuilder.CreateIndex(
                name: "IX_offboardings_CompletedAt_LeavingOn",
                table: "offboardings",
                columns: new[] { "CompletedAt", "LeavingOn" });

            migrationBuilder.CreateIndex(
                name: "IX_offboardings_EmployeeId",
                table: "offboardings",
                column: "EmployeeId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lent_assets");

            migrationBuilder.DropTable(
                name: "offboardings");

            migrationBuilder.DropColumn(
                name: "Education",
                table: "candidates");

            migrationBuilder.DropColumn(
                name: "ExpectedSalaryCurrency",
                table: "candidates");

            migrationBuilder.DropColumn(
                name: "ExpectedSalaryMinorUnits",
                table: "candidates");

            migrationBuilder.DropColumn(
                name: "GitHub",
                table: "candidates");

            migrationBuilder.DropColumn(
                name: "LinkedIn",
                table: "candidates");

            migrationBuilder.DropColumn(
                name: "Portfolio",
                table: "candidates");

            migrationBuilder.DropColumn(
                name: "Skills",
                table: "candidates");

            migrationBuilder.DropColumn(
                name: "YearsOfExperience",
                table: "candidates");
        }
    }
}
