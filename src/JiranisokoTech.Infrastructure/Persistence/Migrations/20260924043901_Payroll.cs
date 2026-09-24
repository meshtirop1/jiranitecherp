using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JiranisokoTech.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Payroll : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pay_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    DraftedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ApprovedById = table.Column<Guid>(type: "uuid", nullable: true),
                    PaidAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pay_runs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "statutory_rates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InForceFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    PersonalReliefMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    PensionEmployeeRate = table.Column<decimal>(type: "numeric(6,5)", precision: 6, scale: 5, nullable: false),
                    PensionEmployerRate = table.Column<decimal>(type: "numeric(6,5)", precision: 6, scale: 5, nullable: false),
                    PensionCeilingMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    HealthRate = table.Column<decimal>(type: "numeric(6,5)", precision: 6, scale: 5, nullable: false),
                    HealthFloorMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    HousingRate = table.Column<decimal>(type: "numeric(6,5)", precision: 6, scale: 5, nullable: false),
                    Source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_statutory_rates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "payslips",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PayRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    GrossMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    PaidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    PaidTo = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payslips", x => x.Id);
                    table.ForeignKey(
                        name: "FK_payslips_pay_runs_PayRunId",
                        column: x => x.PayRunId,
                        principalTable: "pay_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tax_bands",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FromMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    ToMinorUnits = table.Column<long>(type: "bigint", nullable: true),
                    Rate = table.Column<decimal>(type: "numeric(6,5)", precision: 6, scale: 5, nullable: false),
                    StatutoryRatesId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tax_bands", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tax_bands_statutory_rates_StatutoryRatesId",
                        column: x => x.StatutoryRatesId,
                        principalTable: "statutory_rates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "payslip_lines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Basis = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    PayslipId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payslip_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_payslip_lines_payslips_PayslipId",
                        column: x => x.PayslipId,
                        principalTable: "payslips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_pay_runs_one_per_period",
                table: "pay_runs",
                columns: new[] { "PeriodStart", "PeriodEnd" },
                unique: true,
                filter: "\"Status\" <> 4");

            migrationBuilder.CreateIndex(
                name: "IX_payslip_lines_PayslipId",
                table: "payslip_lines",
                column: "PayslipId");

            migrationBuilder.CreateIndex(
                name: "IX_payslips_EmployeeId",
                table: "payslips",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_payslips_one_per_person_per_run",
                table: "payslips",
                columns: new[] { "PayRunId", "EmployeeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_statutory_rates_in_force_from",
                table: "statutory_rates",
                column: "InForceFrom",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tax_bands_StatutoryRatesId",
                table: "tax_bands",
                column: "StatutoryRatesId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payslip_lines");

            migrationBuilder.DropTable(
                name: "tax_bands");

            migrationBuilder.DropTable(
                name: "payslips");

            migrationBuilder.DropTable(
                name: "statutory_rates");

            migrationBuilder.DropTable(
                name: "pay_runs");
        }
    }
}
