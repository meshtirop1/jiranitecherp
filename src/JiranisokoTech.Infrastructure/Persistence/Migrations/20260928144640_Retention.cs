using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiranisokoTech.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Retention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ForgottenAt",
                table: "job_applications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AuditRetentionYears",
                table: "firm_settings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CandidateRetentionMonths",
                table: "firm_settings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LeaverDocumentRetentionYears",
                table: "firm_settings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WithdrawnAccountRetentionMonths",
                table: "firm_settings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ForgottenAt",
                table: "candidates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ForgottenAt",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WithdrawnAt",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ForgottenAt",
                table: "job_applications");

            migrationBuilder.DropColumn(
                name: "AuditRetentionYears",
                table: "firm_settings");

            migrationBuilder.DropColumn(
                name: "CandidateRetentionMonths",
                table: "firm_settings");

            migrationBuilder.DropColumn(
                name: "LeaverDocumentRetentionYears",
                table: "firm_settings");

            migrationBuilder.DropColumn(
                name: "WithdrawnAccountRetentionMonths",
                table: "firm_settings");

            migrationBuilder.DropColumn(
                name: "ForgottenAt",
                table: "candidates");

            migrationBuilder.DropColumn(
                name: "ForgottenAt",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "WithdrawnAt",
                table: "AspNetUsers");
        }
    }
}
