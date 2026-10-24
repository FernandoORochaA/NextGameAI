using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NextGameAI.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCatalogEnrichmentMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CatalogPlatforms",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IgdbId = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogPlatforms", x => x.Id);
                    table.CheckConstraint("CK_CatalogPlatform_IgdbId", "IgdbId > 0");
                    table.CheckConstraint("CK_CatalogPlatform_Name", "length(trim(Name)) > 0");
                });

            migrationBuilder.CreateTable(
                name: "CatalogTaxonomies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    IgdbId = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogTaxonomies", x => x.Id);
                    table.CheckConstraint("CK_CatalogTaxonomy_IgdbId", "IgdbId > 0");
                    table.CheckConstraint("CK_CatalogTaxonomy_Kind", "Kind IN (0,1,2,3,4)");
                    table.CheckConstraint("CK_CatalogTaxonomy_Name", "length(trim(Name)) > 0");
                });

            migrationBuilder.CreateTable(
                name: "GameEnrichmentStates",
                columns: table => new
                {
                    GameId = table.Column<int>(type: "INTEGER", nullable: false),
                    Section = table.Column<int>(type: "INTEGER", nullable: false),
                    LastAttemptAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSuccessAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastResult = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameEnrichmentStates", x => new { x.GameId, x.Section });
                    table.CheckConstraint("CK_GameEnrichmentState_Result", "LastResult IN (0,1,2)");
                    table.CheckConstraint("CK_GameEnrichmentState_Section", "Section IN (0,1,2,3,4,5,6)");
                    table.CheckConstraint("CK_GameEnrichmentState_Source", "length(trim(Source)) > 0");
                    table.CheckConstraint("CK_GameEnrichmentState_Timestamps", "(LastSuccessAtUtc IS NULL OR LastSuccessAtUtc <= LastAttemptAtUtc) AND (LastResult <> 0 OR LastSuccessAtUtc = LastAttemptAtUtc AND LastSuccessAtUtc IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_GameEnrichmentStates_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GameTimeToBeats",
                columns: table => new
                {
                    GameId = table.Column<int>(type: "INTEGER", nullable: false),
                    IgdbId = table.Column<long>(type: "INTEGER", nullable: true),
                    HastilySeconds = table.Column<long>(type: "INTEGER", nullable: true),
                    NormallySeconds = table.Column<long>(type: "INTEGER", nullable: true),
                    CompletelySeconds = table.Column<long>(type: "INTEGER", nullable: true),
                    SubmissionCount = table.Column<int>(type: "INTEGER", nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameTimeToBeats", x => x.GameId);
                    table.CheckConstraint("CK_GameTimeToBeat_IgdbId", "IgdbId IS NULL OR IgdbId > 0");
                    table.CheckConstraint("CK_GameTimeToBeat_Source", "length(trim(Source)) > 0");
                    table.CheckConstraint("CK_GameTimeToBeat_Values", "(HastilySeconds IS NULL OR HastilySeconds >= 0) AND (NormallySeconds IS NULL OR NormallySeconds >= 0) AND (CompletelySeconds IS NULL OR CompletelySeconds >= 0) AND (SubmissionCount IS NULL OR SubmissionCount >= 0)");
                    table.ForeignKey(
                        name: "FK_GameTimeToBeats_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GameMultiplayerModes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GameId = table.Column<int>(type: "INTEGER", nullable: false),
                    IgdbId = table.Column<long>(type: "INTEGER", nullable: true),
                    CatalogPlatformId = table.Column<int>(type: "INTEGER", nullable: true),
                    CampaignCoop = table.Column<bool>(type: "INTEGER", nullable: true),
                    DropIn = table.Column<bool>(type: "INTEGER", nullable: true),
                    LanCoop = table.Column<bool>(type: "INTEGER", nullable: true),
                    OfflineCoop = table.Column<bool>(type: "INTEGER", nullable: true),
                    OnlineCoop = table.Column<bool>(type: "INTEGER", nullable: true),
                    SplitScreen = table.Column<bool>(type: "INTEGER", nullable: true),
                    SplitScreenOnline = table.Column<bool>(type: "INTEGER", nullable: true),
                    OfflineCoopMax = table.Column<int>(type: "INTEGER", nullable: true),
                    OfflineMax = table.Column<int>(type: "INTEGER", nullable: true),
                    OnlineCoopMax = table.Column<int>(type: "INTEGER", nullable: true),
                    OnlineMax = table.Column<int>(type: "INTEGER", nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameMultiplayerModes", x => x.Id);
                    table.CheckConstraint("CK_GameMultiplayerMode_IgdbId", "IgdbId IS NULL OR IgdbId > 0");
                    table.CheckConstraint("CK_GameMultiplayerMode_Players", "(OfflineCoopMax IS NULL OR OfflineCoopMax >= 0) AND (OfflineMax IS NULL OR OfflineMax >= 0) AND (OnlineCoopMax IS NULL OR OnlineCoopMax >= 0) AND (OnlineMax IS NULL OR OnlineMax >= 0)");
                    table.CheckConstraint("CK_GameMultiplayerMode_Source", "length(trim(Source)) > 0");
                    table.ForeignKey(
                        name: "FK_GameMultiplayerModes_CatalogPlatforms_CatalogPlatformId",
                        column: x => x.CatalogPlatformId,
                        principalTable: "CatalogPlatforms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GameMultiplayerModes_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GameTaxonomies",
                columns: table => new
                {
                    GameId = table.Column<int>(type: "INTEGER", nullable: false),
                    CatalogTaxonomyId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameTaxonomies", x => new { x.GameId, x.CatalogTaxonomyId });
                    table.ForeignKey(
                        name: "FK_GameTaxonomies_CatalogTaxonomies_CatalogTaxonomyId",
                        column: x => x.CatalogTaxonomyId,
                        principalTable: "CatalogTaxonomies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GameTaxonomies_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogPlatforms_IgdbId",
                table: "CatalogPlatforms",
                column: "IgdbId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CatalogTaxonomies_Kind_IgdbId",
                table: "CatalogTaxonomies",
                columns: new[] { "Kind", "IgdbId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GameMultiplayerModes_CatalogPlatformId",
                table: "GameMultiplayerModes",
                column: "CatalogPlatformId");

            migrationBuilder.CreateIndex(
                name: "IX_GameMultiplayerModes_GameId",
                table: "GameMultiplayerModes",
                column: "GameId",
                unique: true,
                filter: "CatalogPlatformId IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_GameMultiplayerModes_GameId_CatalogPlatformId",
                table: "GameMultiplayerModes",
                columns: new[] { "GameId", "CatalogPlatformId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GameMultiplayerModes_IgdbId",
                table: "GameMultiplayerModes",
                column: "IgdbId",
                unique: true,
                filter: "IgdbId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_GameTaxonomies_CatalogTaxonomyId",
                table: "GameTaxonomies",
                column: "CatalogTaxonomyId");

            migrationBuilder.CreateIndex(
                name: "IX_GameTimeToBeats_IgdbId",
                table: "GameTimeToBeats",
                column: "IgdbId",
                unique: true,
                filter: "IgdbId IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GameEnrichmentStates");

            migrationBuilder.DropTable(
                name: "GameMultiplayerModes");

            migrationBuilder.DropTable(
                name: "GameTaxonomies");

            migrationBuilder.DropTable(
                name: "GameTimeToBeats");

            migrationBuilder.DropTable(
                name: "CatalogPlatforms");

            migrationBuilder.DropTable(
                name: "CatalogTaxonomies");
        }
    }
}
