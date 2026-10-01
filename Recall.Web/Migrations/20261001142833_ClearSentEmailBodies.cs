using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Recall.Web.Migrations
{
    /// <inheritdoc />
    public partial class ClearSentEmailBodies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data-only: the schema is unchanged. From this version on,
            // EmailRepository.MarkSentAsync erases a message's bodies as it marks
            // it sent; this does the same for everything delivered before that,
            // so no already-sent sign-in link is left readable in the table.
            migrationBuilder.Sql(
                """
                UPDATE email
                SET body = '', html_body = NULL
                WHERE sent_utc IS NOT NULL
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
