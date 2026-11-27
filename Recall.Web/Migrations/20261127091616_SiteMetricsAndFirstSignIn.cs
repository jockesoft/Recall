using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Web.Migrations
{
    /// <inheritdoc />
    public partial class SiteMetricsAndFirstSignIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "first_sign_in_utc",
                table: "app_user",
                type: "timestamp with time zone",
                nullable: true);

            // Accounts from before the column are not new: their first sign-in
            // (or their creation, for one never signed in to) is in the past, so
            // a later sign-in is never counted as a new account.
            migrationBuilder.Sql("UPDATE app_user SET first_sign_in_utc = created_utc WHERE first_sign_in_utc IS NULL;");

            migrationBuilder.CreateTable(
                name: "site_metric_daily",
                columns: table => new
                {
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    count = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_site_metric_daily", x => new { x.date, x.key });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "site_metric_daily");

            migrationBuilder.DropColumn(
                name: "first_sign_in_utc",
                table: "app_user");
        }
    }
}
