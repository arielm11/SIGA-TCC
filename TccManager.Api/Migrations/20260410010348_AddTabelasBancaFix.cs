using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TccManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTabelasBancaFix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Banca",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DataHora = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Local = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TccId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Banca", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Banca_Tccs_TccId",
                        column: x => x.TccId,
                        principalTable: "Tccs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BancaAvaliadores",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BancaId = table.Column<int>(type: "int", nullable: false),
                    ProfessorId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BancaAvaliadores", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BancaAvaliadores_Banca_BancaId",
                        column: x => x.BancaId,
                        principalTable: "Banca",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BancaAvaliadores_usuarios_ProfessorId",
                        column: x => x.ProfessorId,
                        principalTable: "usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Banca_TccId",
                table: "Banca",
                column: "TccId");

            migrationBuilder.CreateIndex(
                name: "IX_BancaAvaliadores_BancaId",
                table: "BancaAvaliadores",
                column: "BancaId");

            migrationBuilder.CreateIndex(
                name: "IX_BancaAvaliadores_ProfessorId",
                table: "BancaAvaliadores",
                column: "ProfessorId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BancaAvaliadores");

            migrationBuilder.DropTable(
                name: "Banca");
        }
    }
}
