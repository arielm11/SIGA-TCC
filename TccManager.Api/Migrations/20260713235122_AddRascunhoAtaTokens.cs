using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TccManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddRascunhoAtaTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rascunho_ata_tokens",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BancaId = table.Column<int>(type: "int", nullable: false),
                    MembroExternoId = table.Column<int>(type: "int", nullable: false),
                    TokenHash = table.Column<string>(type: "char(64)", maxLength: 64, nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rascunho_ata_tokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_rascunho_ata_tokens_Banca_BancaId",
                        column: x => x.BancaId,
                        principalTable: "Banca",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_rascunho_ata_tokens_MembrosExternos_MembroExternoId",
                        column: x => x.MembroExternoId,
                        principalTable: "MembrosExternos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_rascunho_ata_tokens_MembroExternoId",
                table: "rascunho_ata_tokens",
                column: "MembroExternoId");

            migrationBuilder.CreateIndex(
                name: "IX_rascunho_ata_tokens_TokenHash",
                table: "rascunho_ata_tokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_rascunho_ata_tokens_Banca_Membro_Ativo",
                table: "rascunho_ata_tokens",
                columns: new[] { "BancaId", "MembroExternoId" },
                unique: true,
                filter: "[RevokedAtUtc] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rascunho_ata_tokens");
        }
    }
}
