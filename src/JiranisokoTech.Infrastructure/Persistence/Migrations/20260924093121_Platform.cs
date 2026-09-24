using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiranisokoTech.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Platform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ServiceId",
                table: "incidents",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "services",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Matters = table.Column<int>(type: "integer", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: true),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RetiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_services", x => x.Id);
                    table.ForeignKey(
                        name: "FK_services_employees_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_services_repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "resources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Environment = table.Column<int>(type: "integer", nullable: false),
                    ServiceId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExpiresOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RetiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_resources_services_ServiceId",
                        column: x => x.ServiceId,
                        principalTable: "services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_incidents_ServiceId",
                table: "incidents",
                column: "ServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_resources_environment_kind",
                table: "resources",
                columns: new[] { "Environment", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_resources_ExpiresOn",
                table: "resources",
                column: "ExpiresOn");

            migrationBuilder.CreateIndex(
                name: "IX_resources_ServiceId",
                table: "resources",
                column: "ServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_services_Name",
                table: "services",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_services_OwnerId",
                table: "services",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_services_RepositoryId",
                table: "services",
                column: "RepositoryId");

            migrationBuilder.AddForeignKey(
                name: "FK_incidents_services_ServiceId",
                table: "incidents",
                column: "ServiceId",
                principalTable: "services",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_incidents_services_ServiceId",
                table: "incidents");

            migrationBuilder.DropTable(
                name: "resources");

            migrationBuilder.DropTable(
                name: "services");

            migrationBuilder.DropIndex(
                name: "IX_incidents_ServiceId",
                table: "incidents");

            migrationBuilder.DropColumn(
                name: "ServiceId",
                table: "incidents");
        }
    }
}
