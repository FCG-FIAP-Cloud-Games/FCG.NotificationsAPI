using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FCG.Notifications.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class NotificationReconciliationAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notification_reconciliation_audits",
                columns: table => new
                {
                    AuditId = table.Column<Guid>(type: "uuid", nullable: false),
                    NotificationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActionType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PreviousStatus = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    NewStatus = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    OperatorId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    EvidenceReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExpectedUpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DuplicateRiskAcknowledged = table.Column<bool>(type: "boolean", nullable: false),
                    CauseInvestigated = table.Column<bool>(type: "boolean", nullable: false),
                    QuiescenceEvidenceReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_reconciliation_audits", x => x.AuditId);
                    table.ForeignKey(
                        name: "FK_notification_reconciliation_audits_notifications_Notificati~",
                        column: x => x.NotificationId,
                        principalTable: "notifications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_notification_reconciliation_audits_ActionId",
                table: "notification_reconciliation_audits",
                column: "ActionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notification_reconciliation_audits_NotificationId_CreatedAt~",
                table: "notification_reconciliation_audits",
                columns: ["NotificationId", "CreatedAt", "AuditId"]);
            migrationBuilder.Sql("""
                CREATE FUNCTION reject_notification_audit_mutation() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'Notification reconciliation audit is append-only';
                END;
                $$;
                CREATE TRIGGER notification_audit_immutable
                BEFORE UPDATE OR DELETE OR TRUNCATE ON notification_reconciliation_audits
                FOR EACH STATEMENT EXECUTE FUNCTION reject_notification_audit_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_reconciliation_audits");
            migrationBuilder.Sql("DROP FUNCTION reject_notification_audit_mutation();");
        }
    }
}
