using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RetailPOSApi.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddShiftClosingConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_CashierShifts_ClosingCash",
                table: "CashierShifts",
                sql: "[DeclaredCash] IS NULL OR [DeclaredCash] >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CashierShifts_ExpectedCash",
                table: "CashierShifts",
                sql: "[ExpectedCash] IS NULL OR [ExpectedCash] >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_CashierShifts_ClosingCash",
                table: "CashierShifts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CashierShifts_ExpectedCash",
                table: "CashierShifts");
        }
    }
}
