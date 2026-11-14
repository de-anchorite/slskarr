using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Slskarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DownloadJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    HitId = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    RemotePath = table.Column<string>(type: "TEXT", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    BytesDone = table.Column<long>(type: "INTEGER", nullable: false),
                    PlaceInQueue = table.Column<int>(type: "INTEGER", nullable: true),
                    LastProgressAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    FailMessage = table.Column<string>(type: "TEXT", nullable: true),
                    SlskdTransferId = table.Column<Guid>(type: "TEXT", nullable: true),
                    StoragePath = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DownloadJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Hits",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    SearchKey = table.Column<string>(type: "TEXT", nullable: false),
                    Profile = table.Column<string>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    RemotePath = table.Column<string>(type: "TEXT", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    ReleaseTitle = table.Column<string>(type: "TEXT", nullable: false),
                    ParsedAuthor = table.Column<string>(type: "TEXT", nullable: true),
                    ParsedTitle = table.Column<string>(type: "TEXT", nullable: true),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    Format = table.Column<string>(type: "TEXT", nullable: false),
                    FreeSlot = table.Column<bool>(type: "INTEGER", nullable: false),
                    QueueLength = table.Column<int>(type: "INTEGER", nullable: false),
                    UploadSpeed = table.Column<int>(type: "INTEGER", nullable: false),
                    Rank = table.Column<int>(type: "INTEGER", nullable: false),
                    SeenAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Hits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SearchJobs",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Profile = table.Column<string>(type: "TEXT", nullable: false),
                    QueryText = table.Column<string>(type: "TEXT", nullable: false),
                    RequestJson = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastRequestedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    SlskdSearchId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SearchJobs", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DownloadJobs_HitId",
                table: "DownloadJobs",
                column: "HitId");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadJobs_State",
                table: "DownloadJobs",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_Hits_SearchKey_Username_RemotePath",
                table: "Hits",
                columns: new[] { "SearchKey", "Username", "RemotePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Hits_SeenAt",
                table: "Hits",
                column: "SeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_SearchJobs_State",
                table: "SearchJobs",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DownloadJobs");

            migrationBuilder.DropTable(
                name: "Hits");

            migrationBuilder.DropTable(
                name: "SearchJobs");
        }
    }
}
