using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiranisokoTech.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Announcements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "announcements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    ByEmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    NeedsAcknowledgement = table.Column<bool>(type: "boolean", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    WrittenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PostedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExpiresOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(1100)", maxLength: 1100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_announcements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_announcements_departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_announcements_employees_ByEmployeeId",
                        column: x => x.ByEmployeeId,
                        principalTable: "employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "announcement_acknowledgements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AnnouncementId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_announcement_acknowledgements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_announcement_acknowledgements_announcements_AnnouncementId",
                        column: x => x.AnnouncementId,
                        principalTable: "announcements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_announcement_acknowledgements_AnnouncementId_EmployeeId",
                table: "announcement_acknowledgements",
                columns: new[] { "AnnouncementId", "EmployeeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_announcements_ByEmployeeId",
                table: "announcements",
                column: "ByEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_announcements_DepartmentId",
                table: "announcements",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_announcements_up",
                table: "announcements",
                columns: new[] { "State", "ExpiresOn", "DepartmentId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "announcement_acknowledgements");

            migrationBuilder.DropTable(
                name: "announcements");
        }
    }
}
