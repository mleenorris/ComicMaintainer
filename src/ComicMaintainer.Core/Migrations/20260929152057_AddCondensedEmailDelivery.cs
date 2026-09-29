using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicMaintainer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddCondensedEmailDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CondensedFilePaths",
                table: "ComicEmailDeliveries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "ComicEmailDeliveries",
                type: "TEXT",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CondensedFilePaths",
                table: "ComicEmailDeliveries");

            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "ComicEmailDeliveries");
        }
    }
}
