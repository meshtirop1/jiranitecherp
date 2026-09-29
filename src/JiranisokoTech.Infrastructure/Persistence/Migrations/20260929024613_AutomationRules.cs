using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiranisokoTech.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AutomationRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Causation",
                table: "outbox_messages",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "automation_rules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Trigger = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsOn = table.Column<bool>(type: "boolean", nullable: false),
                    DelayMinutes = table.Column<int>(type: "integer", nullable: false),
                    TemplateKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SwitchedOnAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_automation_rules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "automation_actions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Who = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Where = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Days = table.Column<int>(type: "integer", nullable: true),
                    SubscriptionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    RuleId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_automation_actions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_automation_actions_automation_rules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "automation_rules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "automation_conditions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Field = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Operator = table.Column<int>(type: "integer", nullable: false),
                    Value = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    RuleId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_automation_conditions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_automation_conditions_automation_rules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "automation_rules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "automation_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Trigger = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SourceMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Chain = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    MatchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_automation_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_automation_runs_automation_rules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "automation_rules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "automation_run_steps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Describing = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    MadeId = table.Column<Guid>(type: "uuid", nullable: true),
                    DoneAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Refused = table.Column<bool>(type: "boolean", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_automation_run_steps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_automation_run_steps_automation_runs_RunId",
                        column: x => x.RunId,
                        principalTable: "automation_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_automation_actions_RuleId",
                table: "automation_actions",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_automation_conditions_RuleId",
                table: "automation_conditions",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_automation_rules_TemplateKey",
                table: "automation_rules",
                column: "TemplateKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_automation_rules_Trigger_IsOn",
                table: "automation_rules",
                columns: new[] { "Trigger", "IsOn" });

            migrationBuilder.CreateIndex(
                name: "IX_automation_run_steps_RunId",
                table: "automation_run_steps",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_automation_runs_RuleId_MatchedAt",
                table: "automation_runs",
                columns: new[] { "RuleId", "MatchedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_automation_runs_RuleId_SourceMessageId",
                table: "automation_runs",
                columns: new[] { "RuleId", "SourceMessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_automation_runs_Status_DueAt",
                table: "automation_runs",
                columns: new[] { "Status", "DueAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "automation_actions");

            migrationBuilder.DropTable(
                name: "automation_conditions");

            migrationBuilder.DropTable(
                name: "automation_run_steps");

            migrationBuilder.DropTable(
                name: "automation_runs");

            migrationBuilder.DropTable(
                name: "automation_rules");

            migrationBuilder.DropColumn(
                name: "Causation",
                table: "outbox_messages");
        }
    }
}
