using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace NextGameAI.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Games",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IgdbId = table.Column<long>(type: "INTEGER", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    EditionTitle = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Games", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Platforms",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Code = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    IgdbId = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Platforms", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Stores",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Code = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Stores", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LibraryEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                    GameId = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Rating = table.Column<int>(type: "INTEGER", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 5000, nullable: true),
                    AbandonmentReason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    AddedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryEntries", x => x.Id);
                    table.CheckConstraint("CK_LibraryEntry_AbandonmentReason", "Status = 3 OR AbandonmentReason IS NULL");
                    table.CheckConstraint("CK_LibraryEntry_Rating", "Rating IS NULL OR Rating BETWEEN 0 AND 10");
                    table.CheckConstraint("CK_LibraryEntry_Status", "Status IN (0, 1, 2, 3)");
                    table.ForeignKey(
                        name: "FK_LibraryEntries_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LibraryEntries_UserProfiles_UserProfileId",
                        column: x => x.UserProfileId,
                        principalTable: "UserProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GameOwnerships",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LibraryEntryId = table.Column<int>(type: "INTEGER", nullable: false),
                    StoreId = table.Column<int>(type: "INTEGER", nullable: false),
                    PlatformId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameOwnerships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GameOwnerships_LibraryEntries_LibraryEntryId",
                        column: x => x.LibraryEntryId,
                        principalTable: "LibraryEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GameOwnerships_Platforms_PlatformId",
                        column: x => x.PlatformId,
                        principalTable: "Platforms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GameOwnerships_Stores_StoreId",
                        column: x => x.StoreId,
                        principalTable: "Stores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "Platforms",
                columns: new[] { "Id", "Code", "IgdbId", "Name" },
                values: new object[,]
                {
                    { 1, "pc", null, "PC" },
                    { 2, "ps4", null, "PlayStation 4" },
                    { 3, "ps5", null, "PlayStation 5" },
                    { 4, "xbox-one", null, "Xbox One" },
                    { 5, "xbox-series", null, "Xbox Series X/S" },
                    { 6, "switch", null, "Nintendo Switch" }
                });

            migrationBuilder.InsertData(
                table: "Stores",
                columns: new[] { "Id", "Code", "Name" },
                values: new object[,]
                {
                    { 1, "steam", "Steam" },
                    { 2, "gog", "GOG" },
                    { 3, "epic", "Epic Games Store" },
                    { 4, "playstation-store", "PlayStation Store" },
                    { 5, "microsoft-store", "Microsoft Store" },
                    { 6, "nintendo-eshop", "Nintendo eShop" },
                    { 7, "physical", "Mídia física" }
                });

            migrationBuilder.InsertData(
                table: "UserProfiles",
                columns: new[] { "Id", "DisplayName" },
                values: new object[] { 1, "Usuário local" });

            migrationBuilder.CreateIndex(
                name: "IX_GameOwnerships_LibraryEntryId_StoreId_PlatformId",
                table: "GameOwnerships",
                columns: new[] { "LibraryEntryId", "StoreId", "PlatformId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GameOwnerships_PlatformId",
                table: "GameOwnerships",
                column: "PlatformId");

            migrationBuilder.CreateIndex(
                name: "IX_GameOwnerships_StoreId",
                table: "GameOwnerships",
                column: "StoreId");

            migrationBuilder.CreateIndex(
                name: "IX_Games_IgdbId",
                table: "Games",
                column: "IgdbId",
                unique: true,
                filter: "IgdbId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryEntries_GameId",
                table: "LibraryEntries",
                column: "GameId");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryEntries_UserProfileId_GameId",
                table: "LibraryEntries",
                columns: new[] { "UserProfileId", "GameId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LibraryEntries_UserProfileId_Status",
                table: "LibraryEntries",
                columns: new[] { "UserProfileId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Platforms_Code",
                table: "Platforms",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Platforms_IgdbId",
                table: "Platforms",
                column: "IgdbId",
                unique: true,
                filter: "IgdbId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Stores_Code",
                table: "Stores",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GameOwnerships");

            migrationBuilder.DropTable(
                name: "LibraryEntries");

            migrationBuilder.DropTable(
                name: "Platforms");

            migrationBuilder.DropTable(
                name: "Stores");

            migrationBuilder.DropTable(
                name: "Games");

            migrationBuilder.DropTable(
                name: "UserProfiles");
        }
    }
}
