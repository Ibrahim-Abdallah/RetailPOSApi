using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RetailPOSApi.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRefundLifecycleConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RefundPayments_Amount",
                table: "RefundPayments");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RefundPayments_Amount",
                table: "RefundPayments",
                sql: "[Amount] > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RefundLines_Totals",
                table: "RefundLines",
                sql: "[Subtotal] >= 0 AND [DiscountTotal] >= 0 AND [TaxTotal] >= 0 AND [TotalAmount] >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RefundPayments_Amount",
                table: "RefundPayments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RefundLines_Totals",
                table: "RefundLines");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RefundPayments_Amount",
                table: "RefundPayments",
                sql: "[Amount] >= 0");
        }
    }
}
