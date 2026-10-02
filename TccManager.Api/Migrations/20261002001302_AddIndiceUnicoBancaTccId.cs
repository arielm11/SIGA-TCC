using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TccManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddIndiceUnicoBancaTccId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Banca_TccId",
                table: "Banca");

            migrationBuilder.CreateIndex(
                name: "IX_Banca_TccId",
                table: "Banca",
                column: "TccId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Banca_TccId",
                table: "Banca");

            migrationBuilder.CreateIndex(
                name: "IX_Banca_TccId",
                table: "Banca",
                column: "TccId");
        }
    }
}
