using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Web.Migrations
{
    /// <inheritdoc />
    public partial class RemoveAppUserLegacyColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_app_user_user_id",
                table: "app_user");

            migrationBuilder.DropColumn(
                name: "password_hash",
                table: "app_user");

            migrationBuilder.DropColumn(
                name: "user_id",
                table: "app_user");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "password_hash",
                table: "app_user",
                type: "character varying(130)",
                maxLength: 130,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "user_id",
                table: "app_user",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_app_user_user_id",
                table: "app_user",
                column: "user_id",
                unique: true);
        }
    }
}
