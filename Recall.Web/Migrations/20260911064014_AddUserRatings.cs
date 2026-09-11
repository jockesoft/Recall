using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddUserRatings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_rating",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    target_tvdb_id = table.Column<int>(type: "integer", nullable: false),
                    series_tvdb_id = table.Column<int>(type: "integer", nullable: false),
                    value = table.Column<int>(type: "integer", nullable: false),
                    created_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_rating", x => x.id);
                    table.CheckConstraint("ck_user_rating_value_range", "value >= 1 AND value <= 10");
                    table.ForeignKey(
                        name: "FK_user_rating_app_user_user_id",
                        column: x => x.user_id,
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_rating_target_type_target_tvdb_id",
                table: "user_rating",
                columns: new[] { "target_type", "target_tvdb_id" });

            migrationBuilder.CreateIndex(
                name: "IX_user_rating_user_id_series_tvdb_id",
                table: "user_rating",
                columns: new[] { "user_id", "series_tvdb_id" });

            migrationBuilder.CreateIndex(
                name: "IX_user_rating_user_id_target_type",
                table: "user_rating",
                columns: new[] { "user_id", "target_type" });

            migrationBuilder.CreateIndex(
                name: "IX_user_rating_user_id_target_type_target_tvdb_id",
                table: "user_rating",
                columns: new[] { "user_id", "target_type", "target_tvdb_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_rating");
        }
    }
}
