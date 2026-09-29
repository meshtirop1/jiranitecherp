using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiranisokoTech.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AiAssistant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_exchanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    AskedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Feature = table.Column<int>(type: "integer", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    Question = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Lookups = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    Problem = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    InputTokens = table.Column<int>(type: "integer", nullable: false),
                    OutputTokens = table.Column<int>(type: "integer", nullable: false),
                    Milliseconds = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_exchanges", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_exchanges_AccountId_At",
                table: "ai_exchanges",
                columns: new[] { "AccountId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_exchanges_At",
                table: "ai_exchanges",
                column: "At");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_exchanges");
        }
    }
}
