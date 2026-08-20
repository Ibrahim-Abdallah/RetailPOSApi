using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RetailPOSApi.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSaleCompletionIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CompletionIdempotencyKey",
                table: "Sales",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CompletionRequestHash",
                table: "Sales",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompletionIdempotencyKey",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "CompletionRequestHash",
                table: "Sales");
        }
    }
}
