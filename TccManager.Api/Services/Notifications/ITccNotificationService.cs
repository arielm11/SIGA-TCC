namespace TccManager.Api.Services.Notifications;

/// <summary>
/// Fachada semântica consumida pelos controllers: um método por evento de negócio,
/// sem expor destinatários/templates/fila. Contrato: nunca lança exceção — qualquer
/// falha (consulta de destinatário, renderização, fila cheia) vira log Serilog e o
/// método retorna normalmente, garantindo que o fluxo de negócio do controller nunca
/// seja afetado pelo envio de e-mail (RF3/RF4/RNF1).
/// </summary>
public interface ITccNotificationService
{
    /// <summary>Proposta aprovada (RF7) — Aluno. Disparado tanto por AprovarProposta quanto por DesignarOrientador.</summary>
    Task NotificarPropostaAprovadaAsync(int tccId);

    /// <summary>Proposta rejeitada (RF8) — Aluno, com motivo.</summary>
    Task NotificarPropostaRejeitadaAsync(int tccId);

    /// <summary>Banca agendada (RF9) — Aluno, Orientador e avaliadores (internos e externos).</summary>
    Task NotificarBancaAgendadaAsync(int bancaId);

    /// <summary>Feedback de entrega registrado (RF10) — Aluno.</summary>
    Task NotificarFeedbackRegistradoAsync(int entregaId);

    /// <summary>Aceite final concedido (RF11) — Aluno e todos os usuários Tipo = Coordenador.</summary>
    Task NotificarAceiteFinalAsync(int tccId);

    /// <summary>
    /// Resultado final da banca (RF12/RF13) — Aluno e Orientador sempre; se reprovado,
    /// também todos os usuários Tipo = Coordenador. <paramref name="aprovado"/> escolhe
    /// o template (resultado-aprovado vs. resultado-reprovado).
    /// </summary>
    Task NotificarResultadoBancaAsync(int bancaId, bool aprovado);

    /// <summary>
    /// Reenvio de token de acesso ao rascunho (RF-06/Etapa 2) — dispara um e-mail
    /// dedicado para o <c>MembroExterno</c> com o novo link (<paramref name="tokenBruto"/>
    /// já gerado pelo chamador via <c>IRascunhoAtaTokenService.GerarTokenAsync</c>, que
    /// já revogou o token anterior do par).
    /// </summary>
    Task NotificarReenvioRascunhoAsync(int bancaId, int membroExternoId, string tokenBruto);
}
