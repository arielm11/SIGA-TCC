using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TccManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddIndiceUnicoEntregaFinal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Entregas_TccId",
                table: "Entregas");

            migrationBuilder.CreateIndex(
                name: "UX_Entregas_TccId_Final",
                table: "Entregas",
                column: "TccId",
                unique: true,
                filter: "[Tipo] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Entregas_TccId_Final",
                table: "Entregas");

            migrationBuilder.CreateIndex(
                name: "IX_Entregas_TccId",
                table: "Entregas",
                column: "TccId");
        }
    }
}
