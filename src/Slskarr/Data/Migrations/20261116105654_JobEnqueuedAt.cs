using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Slskarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class JobEnqueuedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "EnqueuedAt",
                table: "DownloadJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            // Existing jobs: the last progress is the best available stand-in for the last (re)enqueue.
            migrationBuilder.Sql("UPDATE DownloadJobs SET EnqueuedAt = LastProgressAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnqueuedAt",
                table: "DownloadJobs");
        }
    }
}
