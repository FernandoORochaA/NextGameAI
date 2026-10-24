using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NextGameAI.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class IgdbCatalogMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CoverImageId",
                table: "Games",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "FirstReleaseDate",
                table: "Games",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GameType",
                table: "Games",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "GameTypeId",
                table: "Games",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoverImageId",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "FirstReleaseDate",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "GameType",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "GameTypeId",
                table: "Games");
        }
    }
}
