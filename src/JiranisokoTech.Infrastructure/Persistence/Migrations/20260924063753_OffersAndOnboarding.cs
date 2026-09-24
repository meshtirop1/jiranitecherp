using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiranisokoTech.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OffersAndOnboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "offers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobTitle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SalaryMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    SalaryCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Frequency = table.Column<int>(type: "integer", nullable: false),
                    StartsOn = table.Column<DateOnly>(type: "date", nullable: false),
                    ClosesOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Terms = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    MadeById = table.Column<Guid>(type: "uuid", nullable: false),
                    MadeAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AnsweredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SignedName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Outcome = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_offers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_offers_employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_offers_job_applications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "job_applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "onboardings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartsOn = table.Column<DateOnly>(type: "date", nullable: false),
                    BegunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_onboardings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_onboardings_employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "issued_assets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Identifier = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IssuedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    OnboardingId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_issued_assets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_issued_assets_onboardings_OnboardingId",
                        column: x => x.OnboardingId,
                        principalTable: "onboardings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "onboarding_steps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    DoneAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DoneById = table.Column<Guid>(type: "uuid", nullable: true),
                    OnboardingId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_onboarding_steps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_onboarding_steps_onboardings_OnboardingId",
                        column: x => x.OnboardingId,
                        principalTable: "onboardings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_issued_assets_OnboardingId",
                table: "issued_assets",
                column: "OnboardingId");

            migrationBuilder.CreateIndex(
                name: "IX_offers_ApplicationId",
                table: "offers",
                column: "ApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_offers_EmployeeId",
                table: "offers",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_offers_TokenHash",
                table: "offers",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_onboarding_steps_OnboardingId",
                table: "onboarding_steps",
                column: "OnboardingId");

            migrationBuilder.CreateIndex(
                name: "IX_onboardings_EmployeeId",
                table: "onboardings",
                column: "EmployeeId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "issued_assets");

            migrationBuilder.DropTable(
                name: "offers");

            migrationBuilder.DropTable(
                name: "onboarding_steps");

            migrationBuilder.DropTable(
                name: "onboardings");
        }
    }
}
