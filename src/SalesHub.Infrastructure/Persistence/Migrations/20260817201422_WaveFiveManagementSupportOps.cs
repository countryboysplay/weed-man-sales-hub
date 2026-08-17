using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WaveFiveManagementSupportOps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "archive_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    report_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    blob_id = table.Column<Guid>(type: "uuid", nullable: false),
                    report_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    period_end = table.Column<DateOnly>(type: "date", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recovered = table.Column<bool>(type: "boolean", nullable: false),
                    recovered_from_note = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_archive_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "management_note_ack_targets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    required_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    acknowledged_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_management_note_ack_targets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "management_note_followups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    body = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_management_note_followups", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "management_notes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_id = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    employee_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    priority = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    body = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolution_note = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    pinned_rank = table.Column<int>(type: "integer", nullable: true),
                    acknowledgment_required = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_management_notes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "management_tags",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_management_tags", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "record_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_public_id = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    target_public_id = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    removed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    removed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    remove_reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_record_links", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "remote_device_commands",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_type = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    target_device_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    acknowledged_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_remote_device_commands", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "report_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    schedule_id = table.Column<Guid>(type: "uuid", nullable: true),
                    report_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    period_end = table.Column<DateOnly>(type: "date", nullable: false),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    success = table.Column<bool>(type: "boolean", nullable: false),
                    error = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    artifact_blob_id = table.Column<Guid>(type: "uuid", nullable: true),
                    triggered_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_report_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "report_schedules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    report_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    cadence = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    hour_local = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    next_due_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_run_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_report_schedules", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "support_attachments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    blob_id = table.Column<Guid>(type: "uuid", nullable: false),
                    uploaded_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_support_attachments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "support_collaborators",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    added_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    added_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_support_collaborators", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "support_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_public_id = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_support_links", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "support_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    visibility = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    body = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_support_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "support_tickets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_id = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    reporter_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issue_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    description = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    page = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    app_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    browser_family = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    device_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    priority = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    suggested_priority = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    suggested_priority_reason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    primary_assignee_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    force_closed = table.Column<bool>(type: "boolean", nullable: false),
                    reporter_confirmed_closure = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_support_tickets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sync_actions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    device_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    operation = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    error = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sync_actions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tagged_entities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_public_id = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tagged_entities", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_archive_entries_created_at_utc",
                table: "archive_entries",
                column: "created_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_management_note_ack_targets_note_id_target_user_id",
                table: "management_note_ack_targets",
                columns: new[] { "note_id", "target_user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_management_note_followups_note_id_created_at_utc",
                table: "management_note_followups",
                columns: new[] { "note_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_management_notes_employee_user_id_created_at_utc",
                table: "management_notes",
                columns: new[] { "employee_user_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_management_notes_pinned_rank",
                table: "management_notes",
                column: "pinned_rank",
                filter: "pinned_rank IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_management_notes_public_id",
                table: "management_notes",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_management_tags_label",
                table: "management_tags",
                column: "label",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_record_links_source_public_id",
                table: "record_links",
                column: "source_public_id");

            migrationBuilder.CreateIndex(
                name: "ix_record_links_target_public_id",
                table: "record_links",
                column: "target_public_id");

            migrationBuilder.CreateIndex(
                name: "ix_remote_device_commands_created_at_utc",
                table: "remote_device_commands",
                column: "created_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_remote_device_commands_target_user_id_status",
                table: "remote_device_commands",
                columns: new[] { "target_user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_report_runs_report_type_started_at_utc",
                table: "report_runs",
                columns: new[] { "report_type", "started_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_report_schedules_next_due_at_utc",
                table: "report_schedules",
                column: "next_due_at_utc",
                filter: "enabled");

            migrationBuilder.CreateIndex(
                name: "ix_support_attachments_ticket_id",
                table: "support_attachments",
                column: "ticket_id");

            migrationBuilder.CreateIndex(
                name: "ix_support_collaborators_ticket_id_user_id",
                table: "support_collaborators",
                columns: new[] { "ticket_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_support_links_ticket_id_target_public_id",
                table: "support_links",
                columns: new[] { "ticket_id", "target_public_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_support_messages_ticket_id_created_at_utc",
                table: "support_messages",
                columns: new[] { "ticket_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_support_tickets_public_id",
                table: "support_tickets",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_support_tickets_reporter_user_id_created_at_utc",
                table: "support_tickets",
                columns: new[] { "reporter_user_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_support_tickets_status_priority",
                table: "support_tickets",
                columns: new[] { "status", "priority" });

            migrationBuilder.CreateIndex(
                name: "ix_sync_actions_created_at_utc",
                table: "sync_actions",
                column: "created_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_sync_actions_user_id_created_at_utc",
                table: "sync_actions",
                columns: new[] { "user_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_tagged_entities_entity_public_id",
                table: "tagged_entities",
                column: "entity_public_id");

            migrationBuilder.CreateIndex(
                name: "ix_tagged_entities_tag_id_entity_public_id",
                table: "tagged_entities",
                columns: new[] { "tag_id", "entity_public_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "archive_entries");

            migrationBuilder.DropTable(
                name: "management_note_ack_targets");

            migrationBuilder.DropTable(
                name: "management_note_followups");

            migrationBuilder.DropTable(
                name: "management_notes");

            migrationBuilder.DropTable(
                name: "management_tags");

            migrationBuilder.DropTable(
                name: "record_links");

            migrationBuilder.DropTable(
                name: "remote_device_commands");

            migrationBuilder.DropTable(
                name: "report_runs");

            migrationBuilder.DropTable(
                name: "report_schedules");

            migrationBuilder.DropTable(
                name: "support_attachments");

            migrationBuilder.DropTable(
                name: "support_collaborators");

            migrationBuilder.DropTable(
                name: "support_links");

            migrationBuilder.DropTable(
                name: "support_messages");

            migrationBuilder.DropTable(
                name: "support_tickets");

            migrationBuilder.DropTable(
                name: "sync_actions");

            migrationBuilder.DropTable(
                name: "tagged_entities");
        }
    }
}
