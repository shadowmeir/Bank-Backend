using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bank.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddClientStatusToUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "AspNetUsers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Backfill: existing confirmed users should be Active
            // Active=1, Pending=0 (matches your enum values)
            migrationBuilder.Sql("""
                UPDATE "AspNetUsers"
                SET "Status" = CASE WHEN "EmailConfirmed" = true THEN 1 ELSE 0 END;
            """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Status",
                table: "AspNetUsers");
        }
    }
}