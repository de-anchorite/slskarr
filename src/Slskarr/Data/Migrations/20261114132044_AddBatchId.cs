using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Slskarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBatchId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BatchId",
                table: "DownloadJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Existing jobs used their own id as the slskd batch id.
            migrationBuilder.Sql("UPDATE \"DownloadJobs\" SET \"BatchId\" = \"Id\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BatchId",
                table: "DownloadJobs");
        }
    }
}
