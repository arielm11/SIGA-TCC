using TccManager.Api.Services;
using TccManager.Api.Validators;
using TccManager.Shared.DTOs;
using Xunit;

namespace TccManager.Tests.Validators;

/// <summary>
/// Issue #73 — <see cref="PropostaTccDtoValidator"/> estava vazio; Titulo já tinha
/// [StringLength(200)] via DataAnnotations no próprio DTO, mas Resumo não tinha limite
/// nenhum (nem aqui, nem no model Tcc.Resumo antes desta issue).
/// </summary>
public class PropostaTccDtoValidatorTests
{
    private readonly PropostaTccDtoValidator _validator = new(new HtmlSanitizerService());

    // Issue #76 (D4): PropostaTccDto.OrientadorId foi removido do contrato (era campo morto —
    // nunca validado aqui, nunca lido por TccController.SubmeterProposta). A guarda de que um
    // orientadorId enviado no corpo continua sendo ignorado migrou para o teste de integração
    // TccController_SubmeterProposta_Tests.Bug4_..._DeveSerIgnorado, que envia JSON cru.
    private static PropostaTccDto DtoValido() => new()
    {
        Titulo = "Um Título de TCC",
        Resumo = "Um resumo qualquer."
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
    public void ResumoVazio_DeveFalhar(string? resumo)
    {
        var dto = DtoValido();
        dto.Resumo = resumo!;

        var result = _validator.Validate(dto);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(PropostaTccDto.Resumo)
                                         && e.ErrorMessage == "O resumo é obrigatório.");
    }

    [Fact]
    public void ResumoComExatamente4000Caracteres_DevePassar()
    {
        // Issue #73 — mesmo teto de Tcc.Resumo ([MaxLength(4000)]).
        var dto = DtoValido();
        dto.Resumo = new string('a', 4000);

        var result = _validator.Validate(dto);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ResumoComMaisDe4000Caracteres_DeveFalhar()
    {
        var dto = DtoValido();
        dto.Resumo = new string('a', 4001);

        var result = _validator.Validate(dto);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(PropostaTccDto.Resumo)
                                         && e.ErrorMessage == "O resumo deve ter no máximo 4000 caracteres.");
    }

    [Fact]
    public void ResumoDentroDoLimiteCru_MasQueExpandeAoSanitizar_DeveFalhar()
    {
        // Achado A10-1 da revisão de segurança (docs/seguranca/2026-08-27-fix-campos-texto-livre-maxlength.md):
        // HtmlSanitizer CODIFICA "&" em "&amp;" (5 caracteres) — um Resumo de 4000 "&" passa
        // no limite CRU mas vira 20000 caracteres sanitizados, muito além da coluna
        // nvarchar(4000). A regra precisa medir o valor JÁ sanitizado, não o cru.
        var dto = DtoValido();
        dto.Resumo = new string('&', 4000);

        var result = _validator.Validate(dto);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(PropostaTccDto.Resumo)
                                         && e.ErrorMessage == "O resumo deve ter no máximo 4000 caracteres.");
    }

    [Fact]
    public void TituloComMaisDe200Caracteres_DeveFalhar()
    {
        var dto = DtoValido();
        dto.Titulo = new string('a', 201);

        var result = _validator.Validate(dto);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(PropostaTccDto.Titulo)
                                         && e.ErrorMessage == "O título deve ter no máximo 200 caracteres.");
    }

    [Fact]
    public void TituloComExatamente200Caracteres_DevePassar()
    {
        var dto = DtoValido();
        dto.Titulo = new string('a', 200);

        var result = _validator.Validate(dto);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void TituloDentroDoLimiteCru_MasQueExpandeAoSanitizar_DeveFalhar()
    {
        // Achado A10-1 da revisão de segurança: HtmlSanitizer codifica "&" em "&amp;" (5x
        // maior) — um Titulo de 200 "&" passa no limite cru mas viraria 1000 caracteres
        // sanitizados, estourando a coluna nvarchar(200).
        var dto = DtoValido();
        dto.Titulo = new string('&', 200);

        var result = _validator.Validate(dto);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(PropostaTccDto.Titulo)
                                         && e.ErrorMessage == "O título deve ter no máximo 200 caracteres.");
    }
}
