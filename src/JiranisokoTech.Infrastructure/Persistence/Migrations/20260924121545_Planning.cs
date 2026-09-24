using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiranisokoTech.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Planning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            /*
             * Defaulted to 4 — Task — rather than to zero, and this migration was edited by hand
             * to say so.
             *
             * EF generates defaultValue: 0 for a new non-nullable enum column, and zero is not a
             * value WorkItemKind has: the enum starts at Epic = 1 so that the number is the
             * depth. Every row already in the table came back as whatever the display switch's
             * default arm said, which was "subtask" — so the board showed ten existing cards, an
             * hour after the feature shipped, every one of them labelled the smallest possible
             * kind. Found by opening the page.
             *
             * Task is the right backfill because it is what all of this work already was: the
             * ordinary card, which is exactly what the kind defaults to when somebody raises one.
             */
            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "work_items",
                type: "integer",
                nullable: false,
                defaultValue: 4);

            migrationBuilder.AddColumn<Guid>(
                name: "ParentId",
                table: "work_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SprintId",
                table: "work_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "sprints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Goal = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Starts = table.Column<DateOnly>(type: "date", nullable: false),
                    Ends = table.Column<DateOnly>(type: "date", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CarriedOver = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sprints", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "work_item_comments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ByEmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Body = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EditedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_item_comments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_work_item_comments_work_items_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "work_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "work_item_done_when",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    MetAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MetById = table.Column<Guid>(type: "uuid", nullable: true),
                    DroppedBecause = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_item_done_when", x => x.Id);
                    table.ForeignKey(
                        name: "FK_work_item_done_when_work_items_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "work_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "work_item_labels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_item_labels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_work_item_labels_work_items_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "work_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "work_item_links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BlockerId = table.Column<Guid>(type: "uuid", nullable: false),
                    BlockedId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_item_links", x => x.Id);
                    table.ForeignKey(
                        name: "FK_work_item_links_work_items_BlockedId",
                        column: x => x.BlockedId,
                        principalTable: "work_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_work_item_links_work_items_BlockerId",
                        column: x => x.BlockerId,
                        principalTable: "work_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_work_items_backlog",
                table: "work_items",
                columns: new[] { "SprintId", "Status", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_work_items_ParentId",
                table: "work_items",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_sprints_Starts",
                table: "sprints",
                column: "Starts");

            migrationBuilder.CreateIndex(
                name: "IX_sprints_State",
                table: "sprints",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_work_item_comments_WorkItemId",
                table: "work_item_comments",
                column: "WorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_work_item_done_when_WorkItemId",
                table: "work_item_done_when",
                column: "WorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_work_item_labels_Text",
                table: "work_item_labels",
                column: "Text");

            migrationBuilder.CreateIndex(
                name: "IX_work_item_labels_WorkItemId_Text",
                table: "work_item_labels",
                columns: new[] { "WorkItemId", "Text" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_work_item_links_BlockedId",
                table: "work_item_links",
                column: "BlockedId");

            migrationBuilder.CreateIndex(
                name: "IX_work_item_links_BlockerId_BlockedId",
                table: "work_item_links",
                columns: new[] { "BlockerId", "BlockedId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_work_items_sprints_SprintId",
                table: "work_items",
                column: "SprintId",
                principalTable: "sprints",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_work_items_work_items_ParentId",
                table: "work_items",
                column: "ParentId",
                principalTable: "work_items",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_work_items_sprints_SprintId",
                table: "work_items");

            migrationBuilder.DropForeignKey(
                name: "FK_work_items_work_items_ParentId",
                table: "work_items");

            migrationBuilder.DropTable(
                name: "sprints");

            migrationBuilder.DropTable(
                name: "work_item_comments");

            migrationBuilder.DropTable(
                name: "work_item_done_when");

            migrationBuilder.DropTable(
                name: "work_item_labels");

            migrationBuilder.DropTable(
                name: "work_item_links");

            migrationBuilder.DropIndex(
                name: "IX_work_items_backlog",
                table: "work_items");

            migrationBuilder.DropIndex(
                name: "IX_work_items_ParentId",
                table: "work_items");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "work_items");

            migrationBuilder.DropColumn(
                name: "ParentId",
                table: "work_items");

            migrationBuilder.DropColumn(
                name: "SprintId",
                table: "work_items");
        }
    }
}
