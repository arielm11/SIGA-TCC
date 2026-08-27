using TccManager.Api.Services;
using TccManager.Api.Validators;
using TccManager.Shared.DTOs;
using Xunit;

namespace TccManager.Tests.Validators;

/// <summary>
/// Issue #70, item 4 — <see cref="MembroExternoDtoValidator"/>.
///
/// O DTO alimenta POST/PUT de membros externos e vai direto para colunas NOT NULL
/// (Nome, Email, Instituicao); Email ainda é usado como destinatário de notificação e
/// tem limite de 450 caracteres, mesmo teto do e-mail de usuário. O validator entra em
/// vigor via FluentValidationActionFilter global — aqui exercitamos as regras isoladas.
/// </summary>
public class MembroExternoDtoValidatorTests
{
    private readonly MembroExternoDtoValidator _validator = new(new HtmlSanitizerService());

    private static MembroExternoDto DtoValido() => new()
    {
        Nome = "Maria Avaliadora",
        Email = "maria@universidade.edu.br",
        Instituicao = "Universidade Externa"
    };

    [Fact]
    public void DtoCompleto_DevePassar()
    {
        var result = _validator.Validate(DtoValido());

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NomeVazio_DeveFalhar(string? nome)
    {
        var dto = DtoValido();
        dto.Nome = nome!;

        var result = _validator.Validate(dto);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(MembroExternoDto.Nome)
                                         && e.ErrorMessage == "O nome é obrigatório.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void InstituicaoVazia_DeveFalhar(string? instituicao)
    {
        var dto = DtoValido();
        dto.Instituicao = instituicao!;

        var result = _validator.Validate(dto);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(MembroExternoDto.Instituicao)
                                         && e.ErrorMessage == "A instituição é obrigatória.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmailVazio_DeveFalhar_ComMensagemDeObrigatoriedade(string? email)
    {
        var dto = DtoValido();
        dto.Email = email!;

        var result = _validator.Validate(dto);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(MembroExternoDto.Email)
                                         && e.ErrorMessage == "O email é obrigatório.");
    }

    [Theory]
    [InlineData("nao-e-um-email")]
    [InlineData("sem-arroba.com")]
    [InlineData("@sem-usuario.com")]
    [InlineData("usuario@")]
    [InlineData("a@b@c.com")]
    public void EmailComFormatoInvalido_DeveFalhar(string email)
    {
        var dto = DtoValido();
        dto.Email = email;

        var result = _validator.Validate(dto);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(MembroExternoDto.Email)
                                         && e.ErrorMessage == "O email informado não é válido.");
    }

    [Fact]
    public void NomeComExatamente200Caracteres_DevePassar()
    {
        // Issue #73 — mesmo teto de MembroExterno.Nome ([MaxLength(200)]).
        var dto = DtoValido();
        dto.Nome = new string('a', 200);

        var result = _validator.Validate(dto);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void NomeComMaisDe200Caracteres_DeveFalhar()
    {
        var dto = DtoValido();
        dto.Nome = new string('a', 201);

        var result = _validator.Validate(dto);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(MembroExternoDto.Nome)
                                         && e.ErrorMessage == "O nome deve ter no máximo 200 caracteres.");
    }

    [Fact]
    public void InstituicaoComExatamente300Caracteres_DevePassar()
    {
        // Issue #73 — mesmo teto de MembroExterno.Instituicao ([MaxLength(300)]).
        var dto = DtoValido();
        dto.Instituicao = new string('a', 300);

        var result = _validator.Validate(dto);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void InstituicaoComMaisDe300Caracteres_DeveFalhar()
    {
        var dto = DtoValido();
        dto.Instituicao = new string('a', 301);

        var result = _validator.Validate(dto);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(MembroExternoDto.Instituicao)
                                         && e.ErrorMessage == "A instituição deve ter no máximo 300 caracteres.");
    }

    [Fact]
    public void NomeDentroDoLimiteCru_MasQueExpandeAoSanitizar_DeveFalhar()
    {
        // Achado A10-1 da revisão de segurança — ver PropostaTccDtoValidatorTests para o
        // raciocínio completo (HtmlSanitizer codifica "&" em "&amp;", 5x maior).
        var dto = DtoValido();
        dto.Nome = new string('&', 200);

        var result = _validator.Validate(dto);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(MembroExternoDto.Nome)
                                         && e.ErrorMessage == "O nome deve ter no máximo 200 caracteres.");
    }

    [Fact]
    public void InstituicaoDentroDoLimiteCru_MasQueExpandeAoSanitizar_DeveFalhar()
    {
        var dto = DtoValido();
        dto.Instituicao = new string('&', 300);

        var result = _validator.Validate(dto);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(MembroExternoDto.Instituicao)
                                         && e.ErrorMessage == "A instituição deve ter no máximo 300 caracteres.");
    }

    [Fact]
    public void EmailComExatamente450Caracteres_DevePassar()
    {
        // Limite exato alinhado ao teto usado em UsuarioDtoValidator: sem esta guarda,
        // reduzir o MaximumLength por engano não quebraria a suíte.
        var dto = DtoValido();
        dto.Email = new string('a', 440) + "@teste.com"; // 450 caracteres

        var result = _validator.Validate(dto);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void EmailComMaisDe450Caracteres_DeveFalhar()
    {
        var dto = DtoValido();
        dto.Email = new string('a', 445) + "@teste.com"; // 455 caracteres

        var result = _validator.Validate(dto);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(MembroExternoDto.Email)
                                         && e.ErrorMessage == "O email deve ter no máximo 450 caracteres.");
    }

    [Fact]
    public void TodosOsCamposInvalidos_ReportaOsTresCampos()
    {
        var result = _validator.Validate(new MembroExternoDto());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(MembroExternoDto.Nome));
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(MembroExternoDto.Email));
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(MembroExternoDto.Instituicao));
    }

    [Fact]
    public void IdPreenchido_NaoInfluenciaAValidacao()
    {
        // O Id existe no DTO só para a leitura (GET) e é ignorado na escrita pelo
        // controller; o validator não deve inventar regra sobre ele.
        var dto = DtoValido();
        dto.Id = 987654;

        var result = _validator.Validate(dto);

        Assert.True(result.IsValid);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Achado de segurança A05-1 (docs/seguranca/2026-08-18-fix-notificacoes-email-hardening.md):
    // EmailAddress() do FluentValidation é mais permissivo que o MailboxAddress.Parse
    // usado de fato no envio (MailKitEmailService) — sem o Must abaixo, esses e-mails
    // passariam na validação e só falhariam silenciosamente na hora de enviar.
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("a b@c.com")] // espaço no local-part: EmailAddress() aceita, MimeKit não
    [InlineData("<script>@x.com")]
    public void EmailAceitoPeloEmailAddressMasRejeitadoPeloParserDeEnvio_DeveFalhar(string email)
    {
        var dto = DtoValido();
        dto.Email = email;

        var result = _validator.Validate(dto);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(MembroExternoDto.Email)
                                         && e.ErrorMessage == "O email informado não é aceito pelo servidor de e-mail.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmailNuloOuVazio_NaoLancaAoAvaliarORegraDeParserDeEnvio(string? email)
    {
        // CascadeMode padrão é Continue (nenhum Cascade configurado no validator), então o
        // Must(MailboxAddress.TryParse) roda mesmo depois do NotEmpty já ter falhado —
        // MailboxAddress.TryParse(null, ...) precisa devolver false sem lançar, senão um
        // e-mail vazio no PUT viraria 500 em vez de 400.
        var dto = DtoValido();
        dto.Email = email!;

        var result = _validator.Validate(dto);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(MembroExternoDto.Email)
                                         && e.ErrorMessage == "O email é obrigatório.");
    }
}
