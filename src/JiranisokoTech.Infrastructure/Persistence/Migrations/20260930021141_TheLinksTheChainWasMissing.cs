using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiranisokoTech.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TheLinksTheChainWasMissing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                table: "recurring_expenses",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ContractId",
                table: "projects",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ContractId",
                table: "invoices",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_recurring_expenses_ProjectId",
                table: "recurring_expenses",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_projects_ContractId",
                table: "projects",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_ContractId_Status",
                table: "invoices",
                columns: new[] { "ContractId", "Status" });

            migrationBuilder.AddForeignKey(
                name: "FK_invoices_contracts_ContractId",
                table: "invoices",
                column: "ContractId",
                principalTable: "contracts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_projects_contracts_ContractId",
                table: "projects",
                column: "ContractId",
                principalTable: "contracts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_recurring_expenses_projects_ProjectId",
                table: "recurring_expenses",
                column: "ProjectId",
                principalTable: "projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_invoices_contracts_ContractId",
                table: "invoices");

            migrationBuilder.DropForeignKey(
                name: "FK_projects_contracts_ContractId",
                table: "projects");

            migrationBuilder.DropForeignKey(
                name: "FK_recurring_expenses_projects_ProjectId",
                table: "recurring_expenses");

            migrationBuilder.DropIndex(
                name: "IX_recurring_expenses_ProjectId",
                table: "recurring_expenses");

            migrationBuilder.DropIndex(
                name: "IX_projects_ContractId",
                table: "projects");

            migrationBuilder.DropIndex(
                name: "IX_invoices_ContractId_Status",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "recurring_expenses");

            migrationBuilder.DropColumn(
                name: "ContractId",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "ContractId",
                table: "invoices");
        }
    }
}
