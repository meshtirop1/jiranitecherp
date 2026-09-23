using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiranisokoTech.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StaffRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Details_Address",
                table: "employees",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "Details_DateOfBirth",
                table: "employees",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Details_Location",
                table: "employees",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Details_NationalId",
                table: "employees",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Details_PersonalEmail",
                table: "employees",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Details_Phone",
                table: "employees",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Details_TaxNumber",
                table: "employees",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Details_TimeZone",
                table: "employees",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Emergency_Name",
                table: "employees",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Emergency_Phone",
                table: "employees",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Emergency_Relationship",
                table: "employees",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Terms_Contract",
                table: "employees",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "Terms_EndsOn",
                table: "employees",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Terms_Frequency",
                table: "employees",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Terms_NoticeDays",
                table: "employees",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "Terms_ProbationEndsOn",
                table: "employees",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Terms_SalaryCurrency",
                table: "employees",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Terms_SalaryMinorUnits",
                table: "employees",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "employee_certifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Issuer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IssuedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ExpiresOn = table.Column<DateOnly>(type: "date", nullable: true),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_certifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_employee_certifications_employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "employee_skills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_skills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_employee_skills_employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_employee_certifications_EmployeeId",
                table: "employee_certifications",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_employee_certifications_ExpiresOn",
                table: "employee_certifications",
                column: "ExpiresOn");

            migrationBuilder.CreateIndex(
                name: "IX_employee_skills_EmployeeId",
                table: "employee_skills",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_employee_skills_Name",
                table: "employee_skills",
                column: "Name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "employee_certifications");

            migrationBuilder.DropTable(
                name: "employee_skills");

            migrationBuilder.DropColumn(
                name: "Details_Address",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "Details_DateOfBirth",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "Details_Location",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "Details_NationalId",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "Details_PersonalEmail",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "Details_Phone",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "Details_TaxNumber",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "Details_TimeZone",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "Emergency_Name",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "Emergency_Phone",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "Emergency_Relationship",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "Terms_Contract",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "Terms_EndsOn",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "Terms_Frequency",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "Terms_NoticeDays",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "Terms_ProbationEndsOn",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "Terms_SalaryCurrency",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "Terms_SalaryMinorUnits",
                table: "employees");
        }
    }
}
