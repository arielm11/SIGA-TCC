namespace TccManager.Api.Configuration;

/// <summary>
/// Issue #75 ("limite de tamanho de upload não coberto"): antes desta issue, nenhum endpoint
/// de upload (entrega de TCC, ata de banca) tinha limite de tamanho **explícito** — nem
/// <c>[RequestSizeLimit]</c> por ação, nem <c>MaxRequestBodySize</c> configurado no Kestrel,
/// nem <c>FormOptions.MultipartBodyLengthLimit</c>. Isso NÃO significa corpo de tamanho
/// arbitrário: o Kestrel já aplicava seu próprio default (<c>KestrelServerLimits.MaxRequestBodySize</c>,
/// 30.000.000 bytes ≈ 28,6 MB) por não haver nenhuma configuração de servidor no repositório
/// sobrescrevendo-o — correção feita após revisão de segurança apontar que a formulação
/// original ("sem limite algum") estava incorreta.
///
/// 50 MB é uma decisão deliberada de ELEVAR esse teto implícito, não de introduzir um onde não
/// havia nenhum: cobre com folga um PDF/DOC/DOCX de TCC ou uma ata institucional (documentos de
/// texto, mesmo com imagens embutidas, tipicamente na casa de poucos MB, mas um TCC digitalizado
/// com muitas páginas escaneadas pode se aproximar do teto anterior de ~28,6 MB) e ainda acomoda
/// um ZIP com material complementar. Valor de julgamento próprio (a issue não especifica um
/// número), documentado em docs/implementacao/2026-08-29-bunit-infra-e-gaps-cobertura-teste.md.
/// O ganho real desta issue é tornar o teto **explícito e testável** (por ação, não um default
/// implícito do servidor) — o efeito colateral de também elevá-lo foi uma escolha, não um
/// acidente, mas precisa ser lido como isso: a superfície de corpo grande por requisição destes
/// dois endpoints aumenta, não diminui, em relação ao comportamento anterior.
/// </summary>
public static class UploadLimits
{
    public const long MaxArquivoUploadBytes = 50 * 1024 * 1024; // 50 MB
}
