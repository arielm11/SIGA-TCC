using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TccManager.Api.Migrations
{
    /// <inheritdoc />
    // Issue #81 — ver docs/dados/2026-08-30-reprovacao-durante-orientacao.md, seções 4-6, para
    // o raciocínio completo por trás de cada passo (ordem do Up/Down, backfill, guarda de
    // rollback). O corpo desta migration foi ajustado à mão em relação ao que
    // "dotnet ef migrations add" gerou por padrão, para seguir exatamente a ordem e o SQL
    // especificados naquele documento.
    public partial class AddStatusEntregaEAtualizaIndiceFinal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) Coluna nasce NOT NULL com defaultValue: 0 (StatusEntrega.Pendente) — valor de
            // preenchimento de DDL para as linhas já existentes, já satisfaz a constraint
            // NOT NULL antes de qualquer passo seguinte.
            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "Entregas",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // 2) Backfill semântico: Status = Aprovada (1) para toda Entrega Final (Tipo = 1)
            // cujo Tcc já está em Reprovado/AguardandoDefesa/Finalizado (2, 4, 5) — nesses
            // TCCs o aceite final já foi concedido no passado, então "aprovada" é a leitura
            // historicamente verdadeira (seção 5 do documento de dados). Não insere nem
            // duplica linha nenhuma, só transiciona Pendente(0) -> Aprovada(1) em linhas que
            // já eram as únicas Final daquele TccId (garantido pelo índice único então vigente
            // com filtro "[Tipo] = 1", sem exceção) — não pode violar nenhum dos dois filtros
            // do índice considerados neste arquivo (seção 5.3).
            migrationBuilder.Sql(@"
                UPDATE e
                SET e.Status = 1 -- StatusEntrega.Aprovada
                FROM Entregas e
                INNER JOIN Tccs t ON t.Id = e.TccId
                WHERE e.Tipo = 1 -- TipoEntrega.Final
                  AND t.Status IN (2, 4, 5); -- StatusTcc.Reprovado, AguardandoDefesa, Finalizado
            ");

            // 3) Remove o índice com o filtro antigo (roda o backfill acima ainda com o
            // filtro antigo, mais permissivo, vigente — disciplina defensiva, seção 4.1).
            migrationBuilder.DropIndex(
                name: "UX_Entregas_TccId_Final",
                table: "Entregas");

            // 4) Recria com o novo predicado: invariante passa a ser "no máximo 1 Final NÃO
            // REJEITADA por TCC". "<>" (e não "IN (0, 1)") validado empiricamente contra SQL
            // Server real (seção 3.2 do documento de dados — P-01 da arquitetura resolvida).
            migrationBuilder.CreateIndex(
                name: "UX_Entregas_TccId_Final",
                table: "Entregas",
                column: "TccId",
                unique: true,
                filter: "[Tipo] = 1 AND [Status] <> 2");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 1) Guarda de pré-condição (seção 6 do documento de dados; achado A06-2 da
            // revisão de segurança move este passo para ANTES do DropIndex abaixo): se o
            // mecanismo desta issue já estiver em uso — uma Final Rejeitada convivendo com uma
            // segunda Final Aprovada/Pendente do mesmo TCC —, o índice antigo ("[Tipo] = 1",
            // sem exceção de Status) não tolera as duas linhas e o CREATE UNIQUE INDEX mais
            // abaixo falharia com o erro genérico 1505. Rodar esta checagem primeiro, com o
            // índice novo ainda intacto, evita depender de o SQL Server real envolver o
            // rollback numa transação (garantido sob "dotnet ef database update", mas não sob
            // deploy por script SQL gerado) para que a falha não deixe o banco sem NENHUM dos
            // dois backstops atômicos até intervenção manual. Falha alto e cedo, com mensagem
            // acionável, SEM apagar/mesclar dado nenhum — reconciliar qual Entrega Final
            // permanece por TccId é uma decisão de negócio que exige operador humano, não uma
            // migration de rollback.
            migrationBuilder.Sql(@"
                IF EXISTS (
                    SELECT TccId
                    FROM Entregas
                    WHERE Tipo = 1 -- TipoEntrega.Final
                    GROUP BY TccId
                    HAVING COUNT(*) > 1
                )
                BEGIN
                    RAISERROR(
                        'Rollback bloqueado: existem TCCs com mais de uma entrega FINAL (ex.: uma Rejeitada e uma Aprovada/Pendente resultantes do reenvio pos-rejeicao desta issue). O indice anterior (WHERE [Tipo] = 1, sem excecao de Status) nao tolera isso. Reconcilie manualmente os dados antes de reverter esta migration (ex.: decida, por TccId, qual entrega FINAL deve permanecer, e trate as demais fora deste script) - este rollback nao apaga nem funde dados automaticamente.',
                        16, 1
                    );
                END
            ");

            // 2) Remove o índice com o filtro novo — só alcançado se a guarda acima não abortar.
            migrationBuilder.DropIndex(
                name: "UX_Entregas_TccId_Final",
                table: "Entregas");

            // 3) Restaura o filtro original.
            migrationBuilder.CreateIndex(
                name: "UX_Entregas_TccId_Final",
                table: "Entregas",
                column: "TccId",
                unique: true,
                filter: "[Tipo] = 1");

            // 4) Coluna só é removida depois de o índice antigo (que não a referencia) já
            // estar recriado — ordem simétrica ao Up().
            migrationBuilder.DropColumn(
                name: "Status",
                table: "Entregas");
        }
    }
}
