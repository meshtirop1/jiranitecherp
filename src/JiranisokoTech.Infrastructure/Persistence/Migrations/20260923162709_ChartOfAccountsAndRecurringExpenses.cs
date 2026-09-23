using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiranisokoTech.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChartOfAccountsAndRecurringExpenses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AccountId",
                table: "invoices",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AccountId",
                table: "expense_claims",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "accounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    RetiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "recurring_expenses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Payee = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AmountMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Every = table.Column<int>(type: "integer", nullable: false),
                    StartsOn = table.Column<DateOnly>(type: "date", nullable: false),
                    EndsOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recurring_expenses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_recurring_expenses_accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "recurring_charges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DueOn = table.Column<DateOnly>(type: "date", nullable: false),
                    AmountMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    RaisedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SettledOn = table.Column<DateOnly>(type: "date", nullable: true),
                    RecurringExpenseId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recurring_charges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_recurring_charges_recurring_expenses_RecurringExpenseId",
                        column: x => x.RecurringExpenseId,
                        principalTable: "recurring_expenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_invoices_AccountId",
                table: "invoices",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_Status_IssuedOn",
                table: "invoices",
                columns: new[] { "Status", "IssuedOn" });

            migrationBuilder.CreateIndex(
                name: "IX_expense_claims_AccountId",
                table: "expense_claims",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_accounts_Code",
                table: "accounts",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_accounts_Kind_Code",
                table: "accounts",
                columns: new[] { "Kind", "Code" });

            migrationBuilder.CreateIndex(
                name: "IX_recurring_charges_DueOn",
                table: "recurring_charges",
                column: "DueOn");

            migrationBuilder.CreateIndex(
                name: "IX_recurring_charges_RecurringExpenseId",
                table: "recurring_charges",
                column: "RecurringExpenseId");

            migrationBuilder.CreateIndex(
                name: "IX_recurring_expenses_AccountId",
                table: "recurring_expenses",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_recurring_expenses_Status_StartsOn",
                table: "recurring_expenses",
                columns: new[] { "Status", "StartsOn" });

            migrationBuilder.AddForeignKey(
                name: "FK_expense_claims_accounts_AccountId",
                table: "expense_claims",
                column: "AccountId",
                principalTable: "accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_invoices_accounts_AccountId",
                table: "invoices",
                column: "AccountId",
                principalTable: "accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_expense_claims_accounts_AccountId",
                table: "expense_claims");

            migrationBuilder.DropForeignKey(
                name: "FK_invoices_accounts_AccountId",
                table: "invoices");

            migrationBuilder.DropTable(
                name: "recurring_charges");

            migrationBuilder.DropTable(
                name: "recurring_expenses");

            migrationBuilder.DropTable(
                name: "accounts");

            migrationBuilder.DropIndex(
                name: "IX_invoices_AccountId",
                table: "invoices");

            migrationBuilder.DropIndex(
                name: "IX_invoices_Status_IssuedOn",
                table: "invoices");

            migrationBuilder.DropIndex(
                name: "IX_expense_claims_AccountId",
                table: "expense_claims");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "expense_claims");
        }
    }
}
