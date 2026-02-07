using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TccManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTipoUsuario : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Admin",
                table: "usuarios");

            migrationBuilder.AddColumn<int>(
                name: "Tipo",
                table: "usuarios",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Tipo",
                table: "usuarios");

            migrationBuilder.AddColumn<bool>(
                name: "Admin",
                table: "usuarios",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }
    }
}
