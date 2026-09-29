using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiranisokoTech.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TheAnswerCurrentlyOwed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ticket_messages_audience",
                table: "ticket_messages");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AnswerOwedBy",
                table: "tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AwaitingSince",
                table: "tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_tickets_owed",
                table: "tickets",
                columns: new[] { "AnswerOwedBy", "EscalatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ticket_messages_ticket",
                table: "ticket_messages",
                column: "TicketId");

            /*
             * Hand-written, and without it this migration would silence the desk.
             *
             * AnswerOwedBy is what the escalation sweep and the count beside the navigation
             * link now read, and it arrives null on every row that already exists — so every
             * ticket somebody was already waiting on would become invisible to both,
             * permanently, and the screen would say the firm had nothing outstanding.
             *
             * The values are not invented. A ticket with no first response has been waiting
             * since it arrived, and what it was promised is already stored in RespondBy, so
             * this copies two columns that were computed under the rules in force at the time.
             * A ticket that has been answered is owed nothing at this moment, which is what
             * null means; one that was answered and then chased is picked up the next time
             * somebody records what they said.
             *
             * Status 3 is Resolved.
             */
            migrationBuilder.Sql(
                """
                UPDATE tickets
                   SET "AwaitingSince" = "RaisedAt",
                       "AnswerOwedBy"  = "RespondBy"
                 WHERE "FirstRespondedAt" IS NULL
                   AND "Status" <> 3;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tickets_owed",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "IX_ticket_messages_ticket",
                table: "ticket_messages");

            migrationBuilder.DropColumn(
                name: "AnswerOwedBy",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "AwaitingSince",
                table: "tickets");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_messages_audience",
                table: "ticket_messages",
                columns: new[] { "TicketId", "Audience" });
        }
    }
}
