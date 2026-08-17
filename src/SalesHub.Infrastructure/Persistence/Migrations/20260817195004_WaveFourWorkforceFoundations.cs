using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WaveFourWorkforceFoundations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "custom_status_message",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "presence_status",
                table: "users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "presence_status_changed_at_utc",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_idle_screen_state",
                table: "user_sessions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_idle_user_state",
                table: "user_sessions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "break_correction_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_id = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    break_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_start_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    original_end_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    corrected_start_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    corrected_end_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reviewed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_break_correction_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "break_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    break_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    business_date = table.Column<DateOnly>(type: "date", nullable: false),
                    overrun_flagged = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_break_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "break_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    limit_minutes = table.Column<int>(type: "integer", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_break_types", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "coverage_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    minimum_agents = table.Column<int>(type: "integer", nullable: false),
                    behavior = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_coverage_rules", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "presence_flags",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_id = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    start_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    end_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    source = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    linked_public_ids = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    business_date = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_presence_flags", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "presence_rule_sets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    late_start_grace_minutes = table.Column<int>(type: "integer", nullable: false),
                    offline_grace_minutes = table.Column<int>(type: "integer", nullable: false),
                    serious_offline_minutes = table.Column<int>(type: "integer", nullable: false),
                    break_overrun_grace_minutes = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_presence_rule_sets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "presence_segments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    start_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    end_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_presence_segments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "schedule_exceptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_id = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    replacement_start_local = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    replacement_end_local = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    label = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    suspends_presence = table.Column<bool>(type: "boolean", nullable: false),
                    acknowledgment_required = table.Column<bool>(type: "boolean", nullable: false),
                    acknowledge_by_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    acknowledged_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_schedule_exceptions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "shift_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    day_of_week = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    start_local_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    end_local_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shift_templates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "technical_grants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    technical_report_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    end_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    granted_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_technical_grants", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "technical_reports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_id = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    reporter_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issue_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    page = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    app_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    browser_family = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_technical_reports", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "time_off_cancellation_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    time_off_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    result_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    reviewed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_time_off_cancellation_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "time_off_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_id = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    full_day = table.Column<bool>(type: "boolean", nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    start_local_time = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    end_local_time = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reviewed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    review_note = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    denial_reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    coverage_snapshot_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_time_off_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "time_off_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    paid = table.Column<bool>(type: "boolean", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_time_off_types", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_shift_assignments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shift_template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    assigned_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_shift_assignments", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_break_correction_requests_public_id",
                table: "break_correction_requests",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_break_correction_requests_status",
                table: "break_correction_requests",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_break_sessions_user_id_business_date",
                table: "break_sessions",
                columns: new[] { "user_id", "business_date" });

            migrationBuilder.CreateIndex(
                name: "ux_break_sessions_one_active",
                table: "break_sessions",
                column: "user_id",
                unique: true,
                filter: "ended_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_break_types_label",
                table: "break_types",
                column: "label",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_coverage_rules_role",
                table: "coverage_rules",
                column: "role",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_presence_flags_public_id",
                table: "presence_flags",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_presence_flags_user_id_business_date",
                table: "presence_flags",
                columns: new[] { "user_id", "business_date" });

            migrationBuilder.CreateIndex(
                name: "ix_presence_flags_user_id_category_business_date",
                table: "presence_flags",
                columns: new[] { "user_id", "category", "business_date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_presence_rule_sets_role",
                table: "presence_rule_sets",
                column: "role",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_presence_segments_open",
                table: "presence_segments",
                column: "user_id",
                filter: "end_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_presence_segments_user_id_start_at_utc",
                table: "presence_segments",
                columns: new[] { "user_id", "start_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_schedule_exceptions_public_id",
                table: "schedule_exceptions",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_schedule_exceptions_user_id_date",
                table: "schedule_exceptions",
                columns: new[] { "user_id", "date" });

            migrationBuilder.CreateIndex(
                name: "ix_technical_grants_user_id_start_at_utc",
                table: "technical_grants",
                columns: new[] { "user_id", "start_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_technical_reports_public_id",
                table: "technical_reports",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_time_off_cancellation_requests_time_off_request_id",
                table: "time_off_cancellation_requests",
                column: "time_off_request_id");

            migrationBuilder.CreateIndex(
                name: "ix_time_off_requests_public_id",
                table: "time_off_requests",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_time_off_requests_status_start_date",
                table: "time_off_requests",
                columns: new[] { "status", "start_date" });

            migrationBuilder.CreateIndex(
                name: "ix_time_off_requests_user_id_status",
                table: "time_off_requests",
                columns: new[] { "user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_time_off_types_label",
                table: "time_off_types",
                column: "label",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_shift_assignments_user_id_start_date",
                table: "user_shift_assignments",
                columns: new[] { "user_id", "start_date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "break_correction_requests");

            migrationBuilder.DropTable(
                name: "break_sessions");

            migrationBuilder.DropTable(
                name: "break_types");

            migrationBuilder.DropTable(
                name: "coverage_rules");

            migrationBuilder.DropTable(
                name: "presence_flags");

            migrationBuilder.DropTable(
                name: "presence_rule_sets");

            migrationBuilder.DropTable(
                name: "presence_segments");

            migrationBuilder.DropTable(
                name: "schedule_exceptions");

            migrationBuilder.DropTable(
                name: "shift_templates");

            migrationBuilder.DropTable(
                name: "technical_grants");

            migrationBuilder.DropTable(
                name: "technical_reports");

            migrationBuilder.DropTable(
                name: "time_off_cancellation_requests");

            migrationBuilder.DropTable(
                name: "time_off_requests");

            migrationBuilder.DropTable(
                name: "time_off_types");

            migrationBuilder.DropTable(
                name: "user_shift_assignments");

            migrationBuilder.DropColumn(
                name: "custom_status_message",
                table: "users");

            migrationBuilder.DropColumn(
                name: "presence_status",
                table: "users");

            migrationBuilder.DropColumn(
                name: "presence_status_changed_at_utc",
                table: "users");

            migrationBuilder.DropColumn(
                name: "last_idle_screen_state",
                table: "user_sessions");

            migrationBuilder.DropColumn(
                name: "last_idle_user_state",
                table: "user_sessions");
        }
    }
}
