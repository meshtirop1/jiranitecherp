using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiranisokoTech.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Performance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "goals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ForEmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    SetByEmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Measure = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Detail = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    From = table.Column<DateOnly>(type: "date", nullable: false),
                    To = table.Column<DateOnly>(type: "date", nullable: false),
                    CycleId = table.Column<Guid>(type: "uuid", nullable: true),
                    SetAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Outcome = table.Column<int>(type: "integer", nullable: true),
                    Verdict = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_goals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_goals_employees_ForEmployeeId",
                        column: x => x.ForEmployeeId,
                        principalTable: "employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "review_cycles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    From = table.Column<DateOnly>(type: "date", nullable: false),
                    To = table.Column<DateOnly>(type: "date", nullable: false),
                    IsClosed = table.Column<bool>(type: "boolean", nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_cycles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "goal_notes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ByEmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Note = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    GoalId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_goal_notes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_goal_notes_goals_GoalId",
                        column: x => x.GoalId,
                        principalTable: "goals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "reviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ManagerEmployeeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SelfNote = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    SelfWrittenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ManagerNote = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    ManagerWrittenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Rating = table.Column<int>(type: "integer", nullable: true),
                    SharedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CycleId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_reviews_review_cycles_CycleId",
                        column: x => x.CycleId,
                        principalTable: "review_cycles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_goal_notes_GoalId",
                table: "goal_notes",
                column: "GoalId");

            migrationBuilder.CreateIndex(
                name: "IX_goals_CycleId",
                table: "goals",
                column: "CycleId");

            migrationBuilder.CreateIndex(
                name: "IX_goals_for_open_by",
                table: "goals",
                columns: new[] { "ForEmployeeId", "Outcome", "To" });

            migrationBuilder.CreateIndex(
                name: "IX_review_cycles_IsClosed",
                table: "review_cycles",
                column: "IsClosed");

            migrationBuilder.CreateIndex(
                name: "IX_reviews_CycleId_EmployeeId",
                table: "reviews",
                columns: new[] { "CycleId", "EmployeeId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "goal_notes");

            migrationBuilder.DropTable(
                name: "reviews");

            migrationBuilder.DropTable(
                name: "goals");

            migrationBuilder.DropTable(
                name: "review_cycles");
        }
    }
}
