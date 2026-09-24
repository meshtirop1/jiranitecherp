using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiranisokoTech.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Incidents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "incidents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Severity = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReportedById = table.Column<Guid>(type: "uuid", nullable: false),
                    MitigatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeadId = table.Column<Guid>(type: "uuid", nullable: true),
                    Affects = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Cause = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incidents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_incidents_employees_LeadId",
                        column: x => x.LeadId,
                        principalTable: "employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "incident_notes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    WrittenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ById = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    IncidentId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incident_notes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_incident_notes_incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "postmortems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IncidentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StartedById = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AgreedById = table.Column<Guid>(type: "uuid", nullable: true),
                    AgreedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    WhatHappened = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    WhyItWasPossible = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    HowItWasNoticed = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    WhatWouldHaveCaughtItSooner = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    NothingToDoBecause = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_postmortems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_postmortems_incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "corrective_actions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    AgreedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PostmortemId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_corrective_actions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_corrective_actions_postmortems_PostmortemId",
                        column: x => x.PostmortemId,
                        principalTable: "postmortems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_corrective_actions_PostmortemId",
                table: "corrective_actions",
                column: "PostmortemId");

            migrationBuilder.CreateIndex(
                name: "IX_corrective_actions_WorkItemId",
                table: "corrective_actions",
                column: "WorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_incident_notes_IncidentId_At",
                table: "incident_notes",
                columns: new[] { "IncidentId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_incidents_LeadId",
                table: "incidents",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_incidents_Number",
                table: "incidents",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_incidents_status_started",
                table: "incidents",
                columns: new[] { "Status", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_postmortems_IncidentId",
                table: "postmortems",
                column: "IncidentId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "corrective_actions");

            migrationBuilder.DropTable(
                name: "incident_notes");

            migrationBuilder.DropTable(
                name: "postmortems");

            migrationBuilder.DropTable(
                name: "incidents");
        }
    }
}
