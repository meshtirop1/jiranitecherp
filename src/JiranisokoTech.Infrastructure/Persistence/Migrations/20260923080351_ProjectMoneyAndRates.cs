using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiranisokoTech.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProjectMoneyAndRates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BudgetCurrency",
                table: "projects",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "BudgetMinorUnits",
                table: "projects",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                table: "invoices",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "StandardCostPerHourMinorUnits",
                table: "firm_settings",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                table: "expense_claims",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "exchange_rates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    From = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    To = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Rate = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    On = table.Column<DateOnly>(type: "date", nullable: false),
                    Source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_exchange_rates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_invoices_ProjectId_Status",
                table: "invoices",
                columns: new[] { "ProjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_expense_claims_ProjectId_Status",
                table: "expense_claims",
                columns: new[] { "ProjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_exchange_rates_From_To_On",
                table: "exchange_rates",
                columns: new[] { "From", "To", "On" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_expense_claims_projects_ProjectId",
                table: "expense_claims",
                column: "ProjectId",
                principalTable: "projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_invoices_projects_ProjectId",
                table: "invoices",
                column: "ProjectId",
                principalTable: "projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_expense_claims_projects_ProjectId",
                table: "expense_claims");

            migrationBuilder.DropForeignKey(
                name: "FK_invoices_projects_ProjectId",
                table: "invoices");

            migrationBuilder.DropTable(
                name: "exchange_rates");

            migrationBuilder.DropIndex(
                name: "IX_invoices_ProjectId_Status",
                table: "invoices");

            migrationBuilder.DropIndex(
                name: "IX_expense_claims_ProjectId_Status",
                table: "expense_claims");

            migrationBuilder.DropColumn(
                name: "BudgetCurrency",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "BudgetMinorUnits",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "StandardCostPerHourMinorUnits",
                table: "firm_settings");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "expense_claims");
        }
    }
}
