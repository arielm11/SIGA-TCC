using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TccManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class UpdateBancaAvaliadorExternoFix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "ProfessorId",
                table: "BancaAvaliadores",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddColumn<int>(
                name: "MembroExternoId",
                table: "BancaAvaliadores",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BancaAvaliadores_MembroExternoId",
                table: "BancaAvaliadores",
                column: "MembroExternoId");

            migrationBuilder.AddForeignKey(
                name: "FK_BancaAvaliadores_MembrosExternos_MembroExternoId",
                table: "BancaAvaliadores",
                column: "MembroExternoId",
                principalTable: "MembrosExternos",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BancaAvaliadores_MembrosExternos_MembroExternoId",
                table: "BancaAvaliadores");

            migrationBuilder.DropIndex(
                name: "IX_BancaAvaliadores_MembroExternoId",
                table: "BancaAvaliadores");

            migrationBuilder.DropColumn(
                name: "MembroExternoId",
                table: "BancaAvaliadores");

            migrationBuilder.AlterColumn<int>(
                name: "ProfessorId",
                table: "BancaAvaliadores",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);
        }
    }
}
