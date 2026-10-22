using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // Arrays de colunas gerados pelo EF Core para execução única da migration.

namespace FCG.Notifications.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class PersistentNotificationDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_notifications_EventId_Type",
                table: "notifications");

            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "notifications",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Body",
                table: "notifications",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BusinessOperationId",
                table: "notifications",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FailedAt",
                table: "notifications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailureCode",
                table: "notifications",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastAttemptAt",
                table: "notifications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LogicalRecipientId",
                table: "notifications",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextAttemptAt",
                table: "notifications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReservationExpiresAt",
                table: "notifications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReservationToken",
                table: "notifications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SendingAt",
                table: "notifications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmtpDestinationId",
                table: "notifications",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Subject",
                table: "notifications",
                type: "character varying(998)",
                maxLength: 998,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UnknownAt",
                table: "notifications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "notifications",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            // Não inventar PaymentId, conteúdo ou rota para registros históricos.
            // EventId preserva a distinção anterior; conteúdo null impede despacho automático.
            migrationBuilder.Sql("""
                UPDATE notifications SET
                    "BusinessOperationId" = "EventId"::text,
                    "LogicalRecipientId" = "UserId"::text,
                    "SmtpDestinationId" = 'legacy',
                    "UpdatedAt" = COALESCE("SentAt", "CreatedAt");
                """);

            migrationBuilder.CreateIndex(
                name: "IX_notifications_BusinessOperationId_Type_LogicalRecipientId_S~",
                table: "notifications",
                columns: new[] { "BusinessOperationId", "Type", "LogicalRecipientId", "SmtpDestinationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notifications_EventId_Type",
                table: "notifications",
                columns: new[] { "EventId", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_notifications_Status_NextAttemptAt",
                table: "notifications",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_notifications_Status_ReservationExpiresAt",
                table: "notifications",
                columns: new[] { "Status", "ReservationExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_notifications_BusinessOperationId_Type_LogicalRecipientId_S~",
                table: "notifications");

            migrationBuilder.DropIndex(
                name: "IX_notifications_EventId_Type",
                table: "notifications");

            migrationBuilder.DropIndex(
                name: "IX_notifications_Status_NextAttemptAt",
                table: "notifications");

            migrationBuilder.DropIndex(
                name: "IX_notifications_Status_ReservationExpiresAt",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "AttemptCount",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "Body",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "BusinessOperationId",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "FailedAt",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "FailureCode",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "LastAttemptAt",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "LogicalRecipientId",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "ReservationExpiresAt",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "ReservationToken",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "SendingAt",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "SmtpDestinationId",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "Subject",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "UnknownAt",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "notifications");

            migrationBuilder.CreateIndex(
                name: "IX_notifications_EventId_Type",
                table: "notifications",
                columns: new[] { "EventId", "Type" },
                unique: true);
        }
    }
}
