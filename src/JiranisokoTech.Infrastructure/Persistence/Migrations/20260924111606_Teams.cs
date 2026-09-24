using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiranisokoTech.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Teams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "teams",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Slug = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    LeadEmployeeId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_teams", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "team_members",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    JoinedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    LeftOn = table.Column<DateOnly>(type: "date", nullable: true),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_members", x => x.Id);
                    table.ForeignKey(
                        name: "FK_team_members_teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_team_members_EmployeeId",
                table: "team_members",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_team_members_TeamId",
                table: "team_members",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_teams_IsActive",
                table: "teams",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_teams_LeadEmployeeId",
                table: "teams",
                column: "LeadEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_teams_Slug",
                table: "teams",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "team_members");

            migrationBuilder.DropTable(
                name: "teams");
        }
    }
}
