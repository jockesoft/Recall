using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddAppUserAndTrackedSeriesOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tracked_series_tvdb_id",
                table: "tracked_series");

            migrationBuilder.AddColumn<Guid>(
                name: "user_id",
                table: "tracked_series",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "app_user",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    created_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_user", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tracked_series_user_id_tvdb_id",
                table: "tracked_series",
                columns: new[] { "user_id", "tvdb_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_app_user_email",
                table: "app_user",
                column: "email");

            migrationBuilder.CreateIndex(
                name: "IX_app_user_external_id",
                table: "app_user",
                column: "external_id",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_tracked_series_app_user_user_id",
                table: "tracked_series",
                column: "user_id",
                principalTable: "app_user",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_tracked_series_app_user_user_id",
                table: "tracked_series");

            migrationBuilder.DropTable(
                name: "app_user");

            migrationBuilder.DropIndex(
                name: "IX_tracked_series_user_id_tvdb_id",
                table: "tracked_series");

            migrationBuilder.DropColumn(
                name: "user_id",
                table: "tracked_series");

            migrationBuilder.CreateIndex(
                name: "IX_tracked_series_tvdb_id",
                table: "tracked_series",
                column: "tvdb_id",
                unique: true);
        }
    }
}
