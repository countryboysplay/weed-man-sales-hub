using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WaveSixOwnerSecurityGovernance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "blocked_rollback_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    recorded_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_blocked_rollback_versions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "deployment_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_id = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    success = table.Column<bool>(type: "boolean", nullable: false),
                    notes = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    recorded_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployment_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "emergency_access_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    end_reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_emergency_access_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "known_good_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    recorded_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_known_good_versions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "maintenance_windows",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    end_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    canceled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_maintenance_windows", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "owner_recovery_security_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    detail = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_owner_recovery_security_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "owner_security_configs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    master_credential_hash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    totp_secret_encrypted = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    totp_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failed_attempts = table.Column<int>(type: "integer", nullable: false),
                    locked_until_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_owner_security_configs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "private_communication_access",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    target_conversation_ids_json = table.Column<string>(type: "jsonb", nullable: false),
                    reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    access_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_private_communication_access", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "recovery_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_id = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    archive_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recovery_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "rollback_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_id = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    from_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    to_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    recorded_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rollback_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sensitive_export_access",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    export_id = table.Column<Guid>(type: "uuid", nullable: false),
                    accessed_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    accessed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sensitive_export_access", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sensitive_exports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_id = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    target_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    format = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    blob_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sensitive_exports", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    value_json = table.Column<string>(type: "jsonb", nullable: false),
                    scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "staging_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_id = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    refreshed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_staging_records", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_blocked_rollback_versions_version",
                table: "blocked_rollback_versions",
                column: "version",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_deployment_records_public_id",
                table: "deployment_records",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_emergency_access_sessions_owner_user_id_started_at_utc",
                table: "emergency_access_sessions",
                columns: new[] { "owner_user_id", "started_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_known_good_versions_version",
                table: "known_good_versions",
                column: "version",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_windows_start_at_utc",
                table: "maintenance_windows",
                column: "start_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_owner_recovery_security_events_occurred_at_utc",
                table: "owner_recovery_security_events",
                column: "occurred_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_owner_security_configs_owner_user_id",
                table: "owner_security_configs",
                column: "owner_user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_private_communication_access_access_session_id",
                table: "private_communication_access",
                column: "access_session_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_private_communication_access_owner_user_id",
                table: "private_communication_access",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_recovery_records_public_id",
                table: "recovery_records",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_rollback_records_public_id",
                table: "rollback_records",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sensitive_export_access_export_id_accessed_at_utc",
                table: "sensitive_export_access",
                columns: new[] { "export_id", "accessed_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_sensitive_exports_created_at_utc",
                table: "sensitive_exports",
                column: "created_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_sensitive_exports_public_id",
                table: "sensitive_exports",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_settings_key",
                table: "settings",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_staging_records_public_id",
                table: "staging_records",
                column: "public_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "blocked_rollback_versions");

            migrationBuilder.DropTable(
                name: "deployment_records");

            migrationBuilder.DropTable(
                name: "emergency_access_sessions");

            migrationBuilder.DropTable(
                name: "known_good_versions");

            migrationBuilder.DropTable(
                name: "maintenance_windows");

            migrationBuilder.DropTable(
                name: "owner_recovery_security_events");

            migrationBuilder.DropTable(
                name: "owner_security_configs");

            migrationBuilder.DropTable(
                name: "private_communication_access");

            migrationBuilder.DropTable(
                name: "recovery_records");

            migrationBuilder.DropTable(
                name: "rollback_records");

            migrationBuilder.DropTable(
                name: "sensitive_export_access");

            migrationBuilder.DropTable(
                name: "sensitive_exports");

            migrationBuilder.DropTable(
                name: "settings");

            migrationBuilder.DropTable(
                name: "staging_records");
        }
    }
}
