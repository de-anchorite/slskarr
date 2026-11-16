using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Slskarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class MultiFileReleases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HitFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    HitId = table.Column<string>(type: "TEXT", nullable: false),
                    RemotePath = table.Column<string>(type: "TEXT", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    Subdir = table.Column<string>(type: "TEXT", nullable: false),
                    BitRate = table.Column<int>(type: "INTEGER", nullable: true),
                    BitDepth = table.Column<int>(type: "INTEGER", nullable: true),
                    SampleRate = table.Column<int>(type: "INTEGER", nullable: true),
                    LengthSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    IsVbr = table.Column<bool>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HitFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HitFiles_Hits_HitId",
                        column: x => x.HitId,
                        principalTable: "Hits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JobFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RemotePath = table.Column<string>(type: "TEXT", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    Subdir = table.Column<string>(type: "TEXT", nullable: false),
                    BatchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SlskdTransferId = table.Column<Guid>(type: "TEXT", nullable: true),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    BytesDone = table.Column<long>(type: "INTEGER", nullable: false),
                    PlaceInQueue = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobFiles_DownloadJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "DownloadJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HitFiles_HitId_RemotePath",
                table: "HitFiles",
                columns: new[] { "HitId", "RemotePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobFiles_JobId_RemotePath",
                table: "JobFiles",
                columns: new[] { "JobId", "RemotePath" },
                unique: true);

            // Copy before the job columns go: each existing hit/job becomes a release of exactly one file.
            migrationBuilder.Sql(
                "INSERT INTO HitFiles (HitId, RemotePath, Size, Subdir) SELECT Id, RemotePath, Size, '' FROM Hits");
            migrationBuilder.Sql(
                "INSERT INTO JobFiles (JobId, RemotePath, Size, Subdir, BatchId, SlskdTransferId, State, BytesDone, PlaceInQueue) " +
                "SELECT Id, RemotePath, Size, '', BatchId, SlskdTransferId, State, BytesDone, PlaceInQueue FROM DownloadJobs");

            migrationBuilder.DropColumn(
                name: "BatchId",
                table: "DownloadJobs");

            migrationBuilder.DropColumn(
                name: "SlskdTransferId",
                table: "DownloadJobs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BatchId",
                table: "DownloadJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "SlskdTransferId",
                table: "DownloadJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE DownloadJobs SET " +
                "BatchId = COALESCE((SELECT BatchId FROM JobFiles WHERE JobId = DownloadJobs.Id ORDER BY Id LIMIT 1), BatchId), " +
                "SlskdTransferId = (SELECT SlskdTransferId FROM JobFiles WHERE JobId = DownloadJobs.Id ORDER BY Id LIMIT 1)");

            migrationBuilder.DropTable(
                name: "HitFiles");

            migrationBuilder.DropTable(
                name: "JobFiles");
        }
    }
}
