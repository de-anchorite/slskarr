using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Slskarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class HitUniqueIncludesFormat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Hits_SearchKey_Username_RemotePath",
                table: "Hits");

            migrationBuilder.CreateIndex(
                name: "IX_Hits_SearchKey_Username_RemotePath_Format",
                table: "Hits",
                columns: new[] { "SearchKey", "Username", "RemotePath", "Format" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Hits_SearchKey_Username_RemotePath_Format",
                table: "Hits");

            migrationBuilder.CreateIndex(
                name: "IX_Hits_SearchKey_Username_RemotePath",
                table: "Hits",
                columns: new[] { "SearchKey", "Username", "RemotePath" },
                unique: true);
        }
    }
}
