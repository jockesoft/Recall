using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Web.Migrations
{
    /// <inheritdoc />
    public partial class ClearAbandonedEmailBodies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data-only, the companion to ClearSentEmailBodies: messages that were
            // never delivered and have used up their send attempts. From this
            // version on EmailRepository.RecordFailedAttemptAsync erases those as
            // it records the final failure; this covers the ones that gave up
            // before that.
            //
            // 5 is Mail:MaxSendAttempts as shipped in appsettings (a migration
            // can't read configuration). If an environment has raised that
            // setting, a message between 5 and the higher limit would lose its
            // content while still queued — check before applying there.
            migrationBuilder.Sql(
                """
                UPDATE email
                SET body = '', html_body = NULL
                WHERE sent_utc IS NULL
                  AND send_attempts >= 5
                  AND (body <> '' OR html_body IS NOT NULL);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to restore: the erased content is gone by design.
        }
    }
}
