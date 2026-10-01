using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TccManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddOrientadorSolicitadoIdEmTcc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OrientadorSolicitadoId",
                table: "Tccs",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tccs_OrientadorSolicitadoId",
                table: "Tccs",
                column: "OrientadorSolicitadoId");

            migrationBuilder.AddForeignKey(
                name: "FK_Tccs_usuarios_OrientadorSolicitadoId",
                table: "Tccs",
                column: "OrientadorSolicitadoId",
                principalTable: "usuarios",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tccs_usuarios_OrientadorSolicitadoId",
                table: "Tccs");

            migrationBuilder.DropIndex(
                name: "IX_Tccs_OrientadorSolicitadoId",
                table: "Tccs");

            migrationBuilder.DropColumn(
                name: "OrientadorSolicitadoId",
                table: "Tccs");
        }
    }
}
