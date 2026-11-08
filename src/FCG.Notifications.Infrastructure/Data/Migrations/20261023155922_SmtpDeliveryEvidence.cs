using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FCG.Notifications.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class SmtpDeliveryEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SentConfirmationToken",
                table: "notifications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmtpAcceptance",
                table: "notifications",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmtpMessageId",
                table: "notifications",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            // Identificador prospectivo: não reconstrói headers de envios anteriores à migration.
            migrationBuilder.Sql("""
                UPDATE notifications SET "SmtpMessageId" = 'fcg.' || replace("Id"::text, '-', '') || '@notifications.invalid';
                ALTER TABLE notifications ALTER COLUMN "SmtpMessageId" DROP DEFAULT;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SentConfirmationToken",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "SmtpAcceptance",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "SmtpMessageId",
                table: "notifications");
        }
    }
}
