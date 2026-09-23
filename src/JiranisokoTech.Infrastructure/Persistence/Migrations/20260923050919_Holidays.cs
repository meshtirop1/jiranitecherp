using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiranisokoTech.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Holidays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Days",
                table: "leave_requests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            /*
             * Written by hand, because the default of nought is wrong for every
             * row that already exists and wrong in a way nothing would report. A
             * leave request that has read as five days since it was approved
             * would start reading as none, the screens would show it, and the
             * only symptom is a number quietly disagreeing with the dates next to
             * it — which is exactly the failure the count was moved into a column
             * to avoid.
             *
             * Weekends only, with no holidays deducted, and that is correct
             * rather than a shortcut: public_holidays is created empty by this
             * same migration, so the calendar these requests were agreed against
             * was an empty one. This reproduces the figure the old computed
             * property returned, to the day. Anything more generous would be this
             * migration inventing entitlement nobody granted.
             *
             * isodow puts Monday at 1 and Sunday at 7, so under six is Monday to
             * Friday.
             */
            migrationBuilder.Sql(
                """
                UPDATE leave_requests
                SET "Days" = (
                    SELECT count(*)
                    FROM generate_series("From"::date, "To"::date, interval '1 day') AS day
                    WHERE extract(isodow FROM day) < 6);
                """);

            migrationBuilder.CreateTable(
                name: "public_holidays",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    On = table.Column<DateOnly>(type: "date", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_public_holidays", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_public_holidays_On",
                table: "public_holidays",
                column: "On",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "public_holidays");

            migrationBuilder.DropColumn(
                name: "Days",
                table: "leave_requests");
        }
    }
}
