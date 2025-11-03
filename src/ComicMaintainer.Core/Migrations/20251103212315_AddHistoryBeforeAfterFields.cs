using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComicMaintainer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddHistoryBeforeAfterFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AfterFilename",
                table: "ProcessingHistory",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AfterIssue",
                table: "ProcessingHistory",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AfterPublisher",
                table: "ProcessingHistory",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AfterSeries",
                table: "ProcessingHistory",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AfterTitle",
                table: "ProcessingHistory",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AfterVolume",
                table: "ProcessingHistory",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AfterYear",
                table: "ProcessingHistory",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BeforeFilename",
                table: "ProcessingHistory",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BeforeIssue",
                table: "ProcessingHistory",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BeforePublisher",
                table: "ProcessingHistory",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BeforeSeries",
                table: "ProcessingHistory",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BeforeTitle",
                table: "ProcessingHistory",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BeforeVolume",
                table: "ProcessingHistory",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BeforeYear",
                table: "ProcessingHistory",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AfterFilename",
                table: "ProcessingHistory");

            migrationBuilder.DropColumn(
                name: "AfterIssue",
                table: "ProcessingHistory");

            migrationBuilder.DropColumn(
                name: "AfterPublisher",
                table: "ProcessingHistory");

            migrationBuilder.DropColumn(
                name: "AfterSeries",
                table: "ProcessingHistory");

            migrationBuilder.DropColumn(
                name: "AfterTitle",
                table: "ProcessingHistory");

            migrationBuilder.DropColumn(
                name: "AfterVolume",
                table: "ProcessingHistory");

            migrationBuilder.DropColumn(
                name: "AfterYear",
                table: "ProcessingHistory");

            migrationBuilder.DropColumn(
                name: "BeforeFilename",
                table: "ProcessingHistory");

            migrationBuilder.DropColumn(
                name: "BeforeIssue",
                table: "ProcessingHistory");

            migrationBuilder.DropColumn(
                name: "BeforePublisher",
                table: "ProcessingHistory");

            migrationBuilder.DropColumn(
                name: "BeforeSeries",
                table: "ProcessingHistory");

            migrationBuilder.DropColumn(
                name: "BeforeTitle",
                table: "ProcessingHistory");

            migrationBuilder.DropColumn(
                name: "BeforeVolume",
                table: "ProcessingHistory");

            migrationBuilder.DropColumn(
                name: "BeforeYear",
                table: "ProcessingHistory");
        }
    }
}
