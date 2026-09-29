using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiranisokoTech.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SupportDesk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tickets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Subject = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    From = table.Column<int>(type: "integer", nullable: false),
                    RequesterId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssigneeId = table.Column<Guid>(type: "uuid", nullable: true),
                    RaisedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RespondBy = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolveBy = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FirstRespondedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tickets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tickets_clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ticket_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Audience = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    ByEmployeeId = table.Column<Guid>(type: "uuid", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ticket_messages_tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ticket_messages_audience",
                table: "ticket_messages",
                columns: new[] { "TicketId", "Audience" });

            migrationBuilder.CreateIndex(
                name: "IX_tickets_ClientId",
                table: "tickets",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_tickets_number",
                table: "tickets",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tickets_queue",
                table: "tickets",
                columns: new[] { "Status", "RespondBy" });

            migrationBuilder.CreateIndex(
                name: "IX_tickets_requester",
                table: "tickets",
                column: "RequesterId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ticket_messages");

            migrationBuilder.DropTable(
                name: "tickets");
        }
    }
}
