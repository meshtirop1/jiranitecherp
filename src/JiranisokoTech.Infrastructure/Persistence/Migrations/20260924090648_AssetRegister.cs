using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiranisokoTech.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AssetRegister : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "assets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Tag = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SerialNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    BoughtOn = table.Column<DateOnly>(type: "date", nullable: false),
                    CostMinorUnits = table.Column<long>(type: "bigint", nullable: true),
                    CostCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    WarrantyEndsOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    HeldById = table.Column<Guid>(type: "uuid", nullable: true),
                    HeldSince = table.Column<DateOnly>(type: "date", nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_assets_employees_HeldById",
                        column: x => x.HeldById,
                        principalTable: "employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "asset_movements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    To = table.Column<int>(type: "integer", nullable: false),
                    What = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    PersonId = table.Column<Guid>(type: "uuid", nullable: true),
                    On = table.Column<DateOnly>(type: "date", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asset_movements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_asset_movements_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_asset_movements_AssetId_On",
                table: "asset_movements",
                columns: new[] { "AssetId", "On" });

            migrationBuilder.CreateIndex(
                name: "IX_assets_held_by_status",
                table: "assets",
                columns: new[] { "HeldById", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_assets_Status",
                table: "assets",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_assets_Tag",
                table: "assets",
                column: "Tag",
                unique: true);

            /*
             * Carry the two lists into the register before dropping them.
             *
             * The scaffolder wanted to drop issued_assets and lent_assets outright, which for
             * this repository would have cost a week of nothing — but a firm running this has
             * had section 9's leaver equipment since it was built, and that is exactly the
             * record somebody goes looking for when a laptop cannot be found. Dropping it to
             * make room for a better table would be the migration equivalent of tidying up a
             * timeline.
             *
             * The first common table expression is called "everything" and not "both", which
             * was its name until PostgreSQL refused the migration: BOTH is a reserved word — it
             * is the BOTH in TRIM(BOTH ...) — and the error names the word rather than the
             * reason. Worth the sentence, because the next reserved word to be chosen as an
             * obvious name will fail the same way.
             *
             * Tags are invented, because neither old table had one: CARRIED-0001 upwards,
             * obviously not a real sticker, so that whoever reconciles the register against the
             * shelf knows which rows came from paperwork rather than from a label.
             *
             * A leaver's item that was never returned is issued to them; one that came back is
             * in stock. A joiner's item is issued, unless the same serial number already came
             * across from a leaver's list — the same laptop appearing on both lists is the whole
             * reason this table exists, and it must not become two rows now.
             */
            migrationBuilder.Sql("""
                WITH everything AS (
                    SELECT
                        l."Kind"           AS kind,
                        l."Description"    AS description,
                        l."Identifier"     AS identifier,
                        CASE WHEN l."ReturnedOn" IS NULL THEN o."EmployeeId" END AS held_by,
                        CASE WHEN l."ReturnedOn" IS NULL THEN 2 ELSE 1 END       AS status,
                        o."StartedAt"::date AS on_day
                    FROM lent_assets l
                    JOIN offboardings o ON o."Id" = l."OffboardingId"

                    UNION ALL

                    SELECT
                        i."Kind",
                        i."Description",
                        i."Identifier",
                        n."EmployeeId",
                        2,
                        i."IssuedOn"
                    FROM issued_assets i
                    JOIN onboardings n ON n."Id" = i."OnboardingId"
                ),
                deduped AS (
                    SELECT DISTINCT ON (COALESCE(identifier, description))
                        kind, description, identifier, held_by, status, on_day
                    FROM everything
                    ORDER BY COALESCE(identifier, description), status DESC, on_day
                ),
                numbered AS (
                    SELECT
                        gen_random_uuid() AS id,
                        'CARRIED-' || lpad(
                            (row_number() OVER (ORDER BY on_day, description))::text, 4, '0')
                            AS tag,
                        kind, description, identifier, held_by, status, on_day
                    FROM deduped
                ),
                put AS (
                    INSERT INTO assets
                        ("Id", "Tag", "Kind", "Description", "SerialNumber", "BoughtOn",
                         "Status", "HeldById", "HeldSince")
                    SELECT id, tag, kind, description, identifier, on_day,
                           status, held_by, CASE WHEN status = 2 THEN on_day END
                    FROM numbered
                    RETURNING "Id", "Status", "HeldById", "BoughtOn"
                )
                INSERT INTO asset_movements
                    ("Id", "To", "What", "PersonId", "On", "RecordedAt", "AssetId")
                SELECT
                    gen_random_uuid(),
                    put."Status",
                    'Carried over from the joining and leaving lists',
                    put."HeldById",
                    put."BoughtOn",
                    now(),
                    put."Id"
                FROM put;
                """);

            migrationBuilder.DropTable(
                name: "issued_assets");

            migrationBuilder.DropTable(
                name: "lent_assets");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "asset_movements");

            migrationBuilder.DropTable(
                name: "assets");

            migrationBuilder.CreateTable(
                name: "issued_assets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Identifier = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IssuedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
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
                name: "lent_assets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Condition = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Identifier = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    OffboardingId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReturnedOn = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lent_assets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_lent_assets_offboardings_OffboardingId",
                        column: x => x.OffboardingId,
                        principalTable: "offboardings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_issued_assets_OnboardingId",
                table: "issued_assets",
                column: "OnboardingId");

            migrationBuilder.CreateIndex(
                name: "IX_lent_assets_OffboardingId",
                table: "lent_assets",
                column: "OffboardingId");

            migrationBuilder.CreateIndex(
                name: "IX_lent_assets_ReturnedOn",
                table: "lent_assets",
                column: "ReturnedOn");
        }
    }
}
