using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bank.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCounterpartyAccountIdToLedgerEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CounterpartyAccountId",
                table: "LedgerEntries",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_CorrelationId",
                table: "LedgerEntries",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_CounterpartyAccountId",
                table: "LedgerEntries",
                column: "CounterpartyAccountId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LedgerEntries_CorrelationId",
                table: "LedgerEntries");

            migrationBuilder.DropIndex(
                name: "IX_LedgerEntries_CounterpartyAccountId",
                table: "LedgerEntries");

            migrationBuilder.DropColumn(
                name: "CounterpartyAccountId",
                table: "LedgerEntries");
        }
    }
}
