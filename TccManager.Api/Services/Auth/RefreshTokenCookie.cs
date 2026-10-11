namespace TccManager.Api.Services.Auth;

/// <summary>
/// Issue #171 (migração do refresh token para cookie HttpOnly + SameSite=Strict — ver
/// docs/arquitetura/2026-10-10-refresh-token-cookie-httponly.md, D2/D3). Único ponto do código
/// que conhece nome, <c>Path</c> e atributos do cookie de refresh, e as três operações que o
/// <c>AuthController</c> precisa: ler o valor bruto da requisição, gravar o cookie na resposta
/// (login/refresh) e expirá-lo na resposta (logout). O controller nunca monta
/// <see cref="CookieOptions"/> diretamente — é o que garante que gravação e expiração sempre usam
/// os mesmos atributos (RF04: um cookie expirado com atributos divergentes dos de gravação não é
/// removido pelo navegador, só mascarado).
///
/// Injetável (não estático): depende de <see cref="IWebHostEnvironment"/> (atributo
/// <c>Secure</c>, decisão 5.3 — condicional apenas a <c>IsDevelopment()</c>, nunca a
/// <c>Request.IsHttps</c>/<c>Request.Scheme</c>, ver seção 8.2 do documento de arquitetura) e de
/// <see cref="TimeProvider"/> (cálculo do <c>Max-Age</c>).
///
/// Achado L3 da revisão de segurança de #171 (A05 Security Misconfiguration): nome e <c>Path</c>
/// também são condicionais ao ambiente, pelo mesmo motivo do <c>Secure</c> — fora de
/// <c>Development</c> o cookie usa o prefixo <c>__Host-</c> (<c>__Host-siga_refresh</c>), que a
/// especificação (RFC 6265bis) só permite com <c>Secure=true</c> (garantido fora de Development)
/// e <c>Path=/</c> (não pode ser <c>/api/auth</c> — o prefixo proíbe qualquer Path que não seja a
/// raiz) e sem <c>Domain</c> (já não definíamos). Isso isola o cookie de outros servidores locais
/// em portas diferentes de <c>localhost</c> e de "cookie tossing" por um path mais específico — o
/// nome com prefixo força o navegador a só aceitar/enviar o cookie para a origem exata que o
/// gravou. Em <c>Development</c>, <c>__Host-</c> é impossível (exige <c>Secure=true</c>, e a
/// decisão 5.3 mantém <c>Secure=false</c> em dev para suportar a API rodando sem HTTPS) — o nome
/// e o <c>Path</c> permanecem <c>siga_refresh</c> / <c>{PathBase}/api/auth</c>, como antes. Nome e
/// <c>Path</c> são lidos de propriedades únicas (<see cref="NomeCookie"/> e o <c>Path</c> montado
/// em <see cref="MontarOpcoesBase"/>), nunca duplicados — estende a mesma garantia que D7/D3 já
/// davam para os demais atributos: <c>Ler</c>/<c>Gravar</c>/<c>Expirar</c> nunca divergem.
/// </summary>
public class RefreshTokenCookie
{
    private readonly IWebHostEnvironment _env;
    private readonly TimeProvider _timeProvider;

    public RefreshTokenCookie(IWebHostEnvironment env, TimeProvider timeProvider)
    {
        _env = env;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Nome do cookie, condicional ao ambiente (achado L3): <c>__Host-siga_refresh</c> fora de
    /// <c>Development</c>, <c>siga_refresh</c> em <c>Development</c> (onde o prefixo <c>__Host-</c>
    /// seria rejeitado pelo navegador, já que exige <c>Secure=true</c>). Única fonte de verdade —
    /// <c>Ler</c>, <c>Gravar</c> e <c>Expirar</c> sempre passam por esta propriedade.
    /// </summary>
    private string NomeCookie => _env.IsDevelopment() ? "siga_refresh" : "__Host-siga_refresh";

    /// <summary>
    /// Lê o valor bruto do cookie na requisição. Ausente ou vazio ⇒ <c>null</c> — mesmo
    /// tratamento que o corpo vazio tinha antes da migração (RF03).
    /// </summary>
    public string? Ler(HttpRequest request)
    {
        var valor = request.Cookies[NomeCookie];
        return string.IsNullOrWhiteSpace(valor) ? null : valor;
    }

    /// <summary>
    /// Grava o cookie na resposta (login e refresh bem-sucedido). <c>Max-Age</c> é calculado a
    /// partir da expiração real do refresh token no banco (<paramref name="expiraEmUtc"/>), via
    /// <see cref="TimeProvider"/> — nunca negativo, mesmo que o valor já tenha expirado por algum
    /// motivo (defensivo; não deveria acontecer no caminho normal).
    /// </summary>
    public void Gravar(HttpResponse response, string valorBruto, DateTime expiraEmUtc)
    {
        var agora = _timeProvider.GetUtcNow().UtcDateTime;
        var maxAge = expiraEmUtc > agora ? expiraEmUtc - agora : TimeSpan.Zero;

        var opcoes = MontarOpcoesBase(response.HttpContext);
        opcoes.MaxAge = maxAge;

        response.Cookies.Append(NomeCookie, valorBruto, opcoes);
    }

    /// <summary>
    /// Expira o cookie na resposta (logout — RF04). Usa exatamente os mesmos atributos de
    /// <see cref="Gravar"/> (<c>Path</c>, <c>SameSite</c>, <c>Secure</c>, <c>HttpOnly</c>);
    /// <c>HttpResponse.Cookies.Delete</c> sobrescreve apenas <c>Expires</c>/<c>Max-Age</c> para
    /// expirar, preservando o restante das opções informadas — é o que garante que o cookie seja
    /// de fato removido pelo navegador, não apenas mascarado por um <c>Set-Cookie</c> com atributos
    /// diferentes do original.
    /// </summary>
    public void Expirar(HttpResponse response)
    {
        response.Cookies.Delete(NomeCookie, MontarOpcoesBase(response.HttpContext));
    }

    private CookieOptions MontarOpcoesBase(HttpContext context) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        // 5.3: Secure=false SÓ em Development — nunca condicionado a Request.IsHttps/Scheme
        // (8.2 do documento de arquitetura: quem decide se envia um cookie Secure é o navegador,
        // com base na conexão dele, não no esquema visto pela API atrás de um eventual proxy).
        // NOSONAR (csharpsquid:S2092): falso positivo — a análise estática não enxerga que
        // Secure=false só é alcançável em Development (decisão 5.3, confirmada pelo usuário);
        // fora de Development este valor é sempre true.
        Secure = !_env.IsDevelopment(), // NOSONAR
        // D2/L3: em Development, restringe o cookie a /api/auth — não viaja em toda requisição à
        // API, só nos endpoints que de fato o leem/gravam/expiram. Montado com PathBase para não
        // quebrar silenciosamente se a API for publicada sob um prefixo de caminho. Fora de
        // Development o nome ganha o prefixo __Host- (ver NomeCookie), que PROÍBE qualquer Path
        // que não seja a raiz — não há como manter Path=/api/auth junto do prefixo.
        Path = _env.IsDevelopment() ? $"{context.Request.PathBase}/api/auth" : "/",
        // Domain propositalmente não definido: cookie host-only, nunca enviado a subdomínios
        // irmãos do host da API (e exigido por __Host- fora de Development).
    };
}
