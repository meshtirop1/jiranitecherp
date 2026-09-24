using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiranisokoTech.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Privacy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "privacy_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Reference = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Ask = table.Column<int>(type: "integer", nullable: false),
                    SubjectKind = table.Column<int>(type: "integer", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    Subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ReceivedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    RaisedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AnsweredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Note = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_privacy_requests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "privacy_outcomes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Held = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Basis = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Until = table.Column<DateOnly>(type: "date", nullable: true),
                    DecidedById = table.Column<Guid>(type: "uuid", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PrivacyRequestId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_privacy_outcomes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_privacy_outcomes_privacy_requests_PrivacyRequestId",
                        column: x => x.PrivacyRequestId,
                        principalTable: "privacy_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_privacy_outcomes_PrivacyRequestId_Held",
                table: "privacy_outcomes",
                columns: new[] { "PrivacyRequestId", "Held" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_privacy_outcomes_Until",
                table: "privacy_outcomes",
                column: "Until");

            migrationBuilder.CreateIndex(
                name: "IX_privacy_requests_AnsweredAt_ReceivedOn",
                table: "privacy_requests",
                columns: new[] { "AnsweredAt", "ReceivedOn" });

            migrationBuilder.CreateIndex(
                name: "IX_privacy_requests_Reference",
                table: "privacy_requests",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_privacy_requests_SubjectId",
                table: "privacy_requests",
                column: "SubjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "privacy_outcomes");

            migrationBuilder.DropTable(
                name: "privacy_requests");
        }
    }
}
