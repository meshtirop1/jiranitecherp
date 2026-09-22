using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JiranisokoTech.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Recruitment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "candidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FullName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Phone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_candidates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "job_requisitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobTitle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Headcount = table.Column<int>(type: "integer", nullable: false),
                    HiredCount = table.Column<int>(type: "integer", nullable: false),
                    Justification = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    RaisedById = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_requisitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_job_requisitions_departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "job_postings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequisitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Summary = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    Description = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    Location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClosesOn = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_postings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_job_postings_job_requisitions_RequisitionId",
                        column: x => x.RequisitionId,
                        principalTable: "job_requisitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "job_applications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PostingId = table.Column<Guid>(type: "uuid", nullable: false),
                    CandidateId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppliedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    RejectionReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_applications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_job_applications_candidates_CandidateId",
                        column: x => x.CandidateId,
                        principalTable: "candidates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_job_applications_job_postings_PostingId",
                        column: x => x.PostingId,
                        principalTable: "job_postings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_candidates_Email",
                table: "candidates",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_job_applications_CandidateId",
                table: "job_applications",
                column: "CandidateId");

            migrationBuilder.CreateIndex(
                name: "IX_job_applications_PostingId_CandidateId",
                table: "job_applications",
                columns: new[] { "PostingId", "CandidateId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_job_applications_PostingId_Status",
                table: "job_applications",
                columns: new[] { "PostingId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_job_postings_RequisitionId",
                table: "job_postings",
                column: "RequisitionId");

            migrationBuilder.CreateIndex(
                name: "IX_job_postings_Slug",
                table: "job_postings",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_job_postings_Status_PublishedAt",
                table: "job_postings",
                columns: new[] { "Status", "PublishedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_job_requisitions_DepartmentId",
                table: "job_requisitions",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_job_requisitions_Status",
                table: "job_requisitions",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "job_applications");

            migrationBuilder.DropTable(
                name: "candidates");

            migrationBuilder.DropTable(
                name: "job_postings");

            migrationBuilder.DropTable(
                name: "job_requisitions");
        }
    }
}
