using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TccManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddIndiceUnicoEmailUsuarios : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Achado SonarCloud (csharpsquid:S8747, 2026-10-11) — mesmo raciocínio da migration
            // AddMaxLengthCamposTextoLivre: trunca qualquer valor já persistido mais longo que o
            // novo limite antes do ALTER COLUMN, para o narrowing nunca falhar num banco
            // existente. Só afeta bootstraps futuros a partir do zero.
            migrationBuilder.Sql("UPDATE [usuarios] SET [Email] = LEFT([Email], 450) WHERE LEN([Email]) > 450;");
            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "usuarios",
                type: "nvarchar(450)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.CreateIndex(
                name: "IX_usuarios_Email",
                table: "usuarios",
                column: "Email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_usuarios_Email",
                table: "usuarios");

            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "usuarios",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");
        }
    }
}
