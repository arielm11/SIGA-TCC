using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TccManager.Api.Migrations
{
    /// <inheritdoc />
    // Issue #82 — ver docs/dados/2026-08-30-status-tcc-emandamento-nao-usado.md (seções 3-4) e
    // docs/arquitetura/2026-08-30-status-tcc-emandamento-nao-usado.md (D10/D11, seção 11) para o
    // raciocínio completo. Esta migration é puramente de dados: "dotnet ef migrations add" não
    // detecta nenhuma mudança de model (nenhuma coluna, índice, FK ou tabela nova — StatusTcc já
    // é armazenado como int puro desde a AddTccTable), então o Up/Down gerados vieram vazios e
    // foram escritos à mão para bater exatamente com o SQL especificado naquele documento.
    public partial class BackfillStatusTccEmAndamento : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) Direção principal — Aprovado(1) -> EmAndamento(3) para TCCs que já têm ≥1
            // Entrega: ficaram presos em Aprovado porque o gatilho automático (D2,
            // TccController.EnviarEntrega) não existia até esta issue. Depois desta migration,
            // EmAndamento é a leitura historicamente correta para eles.
            migrationBuilder.Sql(@"
                UPDATE t
                SET t.Status = 3 -- StatusTcc.EmAndamento
                FROM Tccs t
                WHERE t.Status = 1 -- StatusTcc.Aprovado
                  AND EXISTS (SELECT 1 FROM Entregas e WHERE e.TccId = t.Id);
            ");

            // 2) Direção simétrica — EmAndamento(3) -> Aprovado(1) para TCCs sem nenhuma
            // Entrega (RF-04): nenhum caminho de código de produção jamais atribuiu
            // EmAndamento (só massas de teste), mas corrige qualquer linha gravada
            // manualmente/via seed em ambientes de dev/produção que a leitura de código não
            // alcança. Idempotente e inofensivo mesmo que não exista nenhuma linha assim: o
            // EXISTS não encontra nada e o UPDATE afeta zero linhas.
            migrationBuilder.Sql(@"
                UPDATE t
                SET t.Status = 1 -- StatusTcc.Aprovado
                FROM Tccs t
                WHERE t.Status = 3 -- StatusTcc.EmAndamento
                  AND NOT EXISTS (SELECT 1 FROM Entregas e WHERE e.TccId = t.Id);
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reversão direcionada da transição principal (item 1 do Up) — NÃO uma reversão
            // genérica de "todo Status = 3". Necessária porque o código anterior a esta issue
            // (Grupo B, igualdade exata com Aprovado) recusa uploads de um TCC em EmAndamento:
            // um rollback de código sem rollback de dado deixaria aluno travado, o sintoma
            // exato que esta issue existe para eliminar.
            //
            // Limitação de reversibilidade (não é uma inversa exata do Up, documentado em
            // docs/dados/2026-08-30-status-tcc-emandamento-nao-usado.md, seção 4): este Down()
            // restaura o comportamento do gate de upload anterior à issue #82 (nenhum TCC com
            // entrega fica preso em EmAndamento), mas TCCs que o item 2 do Up() corrigiu de
            // EmAndamento (sem entrega) para Aprovado NÃO voltam a EmAndamento — não há como
            // distingui-los, só pelos dados, de um TCC que sempre esteve em Aprovado, e
            // nenhuma coluna de auditoria foi introduzida para rastrear isso (decisão
            // deliberada: zero mudança de schema, D11 da arquitetura). Sob a semântica
            // anterior a esta issue, isso é inofensivo — EmAndamento não era alcançado por
            // nenhum caminho de produção antes desta migration. Sem RAISERROR/guarda de
            // pré-condição aqui: ao contrário da migration da #81, nenhum índice/constraint
            // depende de Tcc.Status, então este UPDATE não pode falhar por integridade
            // referencial ou de unicidade em nenhum cenário de dados.
            migrationBuilder.Sql(@"
                UPDATE t
                SET t.Status = 1 -- StatusTcc.Aprovado
                FROM Tccs t
                WHERE t.Status = 3 -- StatusTcc.EmAndamento
                  AND EXISTS (SELECT 1 FROM Entregas e WHERE e.TccId = t.Id);
            ");
        }
    }
}
