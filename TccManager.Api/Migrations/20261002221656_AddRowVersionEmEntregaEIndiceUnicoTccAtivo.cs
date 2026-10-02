using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TccManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddRowVersionEmEntregaEIndiceUnicoTccAtivo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tccs_AlunoId",
                table: "Tccs");

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "Entregas",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.CreateIndex(
                name: "UX_Tccs_AlunoId_Ativo",
                table: "Tccs",
                column: "AlunoId",
                unique: true,
                filter: "[Status] <> 2");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Tccs_AlunoId_Ativo",
                table: "Tccs");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "Entregas");

            migrationBuilder.CreateIndex(
                name: "IX_Tccs_AlunoId",
                table: "Tccs",
                column: "AlunoId");
        }
    }
}
