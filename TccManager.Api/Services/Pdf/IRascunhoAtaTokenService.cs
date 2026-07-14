namespace TccManager.Api.Services.Pdf;

public enum RascunhoTokenValidacaoStatus
{
    /// <summary>Token existe, não foi revogado, ainda não expirou por data e o resultado não foi registrado.</summary>
    Valido,

    /// <summary>
    /// Token inexistente, revogado ou expirado por data. Deliberadamente unificado em um
    /// único status (nunca detalhado ao chamador anônimo) para não vazar existência/estado
    /// de tokens — ver docs/arquitetura/2026-07-13-pdf-ata-rascunho-etapa2.md, seção 5.
    /// </summary>
    Invalido,

    /// <summary>Banca.NotaFinal já foi preenchido — bloqueio definitivo (RNF-03), independente da validade por data.</summary>
    ResultadoRegistrado
}

/// <summary>Resultado da validação de um token de acesso ao rascunho, consumido pelo endpoint público.</summary>
public class RascunhoTokenValidacao
{
    public required RascunhoTokenValidacaoStatus Status { get; init; }
    public int BancaId { get; init; }
}

/// <summary>
/// Emissão, validação e revogação do token opaco de acesso externo ao rascunho da ata
/// (N2 Etapa 2). Segue o espírito de <c>AuthTokenService</c>: CSPRNG (32 bytes), hash
/// SHA-256 armazenado (nunca o valor bruto), expiração e revogação. Nenhum controller
/// manipula hash/CSPRNG diretamente — toda a lógica de token fica concentrada aqui.
/// </summary>
public interface IRascunhoAtaTokenService
{
    /// <summary>
    /// Revoga qualquer token vigente do par (idempotência do reenvio) e cria um novo,
    /// com <c>ExpiresAtUtc</c> = <c>Banca.DataHora</c>. Retorna o valor bruto do token
    /// (existe apenas em memória, nunca persistido) para uso imediato na montagem do
    /// link do e-mail.
    /// </summary>
    Task<string> GerarTokenAsync(int bancaId, int membroExternoId);

    /// <summary>
    /// Calcula o hash do token recebido e busca por hash (nunca compara pelo valor bruto).
    /// </summary>
    Task<RascunhoTokenValidacao> ValidarAsync(string tokenBruto);

    /// <summary>Revoga o token vigente do par, se houver. Usado isoladamente pelo reenvio (RF-06).</summary>
    Task RevogarTokenAtualAsync(int bancaId, int membroExternoId);
}
