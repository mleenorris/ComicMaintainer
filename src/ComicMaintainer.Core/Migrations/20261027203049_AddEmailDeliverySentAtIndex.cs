using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicMaintainer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailDeliverySentAtIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ComicEmailDeliveries_Status_SentAt",
                table: "ComicEmailDeliveries",
                columns: new[] { "Status", "SentAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ComicEmailDeliveries_Status_SentAt",
                table: "ComicEmailDeliveries");
        }
    }
}
