using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WaveTwoSales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sale_corrections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sale_id = table.Column<Guid>(type: "uuid", nullable: false),
                    correction_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    before_json = table.Column<string>(type: "jsonb", nullable: false),
                    after_json = table.Column<string>(type: "jsonb", nullable: false),
                    reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sale_corrections", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sale_duplicate_overrides",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sale_id = table.Column<Guid>(type: "uuid", nullable: false),
                    prior_sale_id = table.Column<Guid>(type: "uuid", nullable: false),
                    confirmed_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    confirmed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sale_duplicate_overrides", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sales",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    seller_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cid = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    sale_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    campaign = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    business_date = table.Column<DateOnly>(type: "date", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    deleted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sales", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sale_corrections_sale_id",
                table: "sale_corrections",
                column: "sale_id");

            migrationBuilder.CreateIndex(
                name: "ix_sale_duplicate_overrides_prior_sale_id",
                table: "sale_duplicate_overrides",
                column: "prior_sale_id");

            migrationBuilder.CreateIndex(
                name: "ix_sale_duplicate_overrides_sale_id",
                table: "sale_duplicate_overrides",
                column: "sale_id");

            migrationBuilder.CreateIndex(
                name: "ix_sales_business_date",
                table: "sales",
                column: "business_date");

            migrationBuilder.CreateIndex(
                name: "ix_sales_cid_business_date",
                table: "sales",
                columns: new[] { "cid", "business_date" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_seller_user_id_business_date",
                table: "sales",
                columns: new[] { "seller_user_id", "business_date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sale_corrections");

            migrationBuilder.DropTable(
                name: "sale_duplicate_overrides");

            migrationBuilder.DropTable(
                name: "sales");
        }
    }
}
