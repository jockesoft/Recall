using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddWatchlistImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "watchlist_import_job",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    total_count = table.Column<int>(type: "integer", nullable: false),
                    processed_count = table.Column<int>(type: "integer", nullable: false),
                    imported_count = table.Column<int>(type: "integer", nullable: false),
                    skipped_count = table.Column<int>(type: "integer", nullable: false),
                    not_found_count = table.Column<int>(type: "integer", nullable: false),
                    failed_count = table.Column<int>(type: "integer", nullable: false),
                    created_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_watchlist_import_job", x => x.id);
                    table.ForeignKey(
                        name: "FK_watchlist_import_job_app_user_user_id",
                        column: x => x.user_id,
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "watchlist_import_item",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_number = table.Column<int>(type: "integer", nullable: false),
                    imdb_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    title_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    your_rating = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    resolved_tvdb_id = table.Column<int>(type: "integer", nullable: true),
                    result_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    processed_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_watchlist_import_item", x => x.id);
                    table.ForeignKey(
                        name: "FK_watchlist_import_item_watchlist_import_job_job_id",
                        column: x => x.job_id,
                        principalTable: "watchlist_import_job",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_watchlist_import_item_job_id",
                table: "watchlist_import_item",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "IX_watchlist_import_item_status_created_utc",
                table: "watchlist_import_item",
                columns: new[] { "status", "created_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_watchlist_import_job_user_id_created_utc",
                table: "watchlist_import_job",
                columns: new[] { "user_id", "created_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "watchlist_import_item");

            migrationBuilder.DropTable(
                name: "watchlist_import_job");
        }
    }
}
