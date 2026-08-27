using TccManager.Api.Services;
using TccManager.Api.Validators;
using TccManager.Shared.DTOs;
using Xunit;

namespace TccManager.Tests.Validators;

/// <summary>
/// Issue #73 — <see cref="AcompanhamentoDtoValidator"/> estava vazio (só o
/// [Required] via DataAnnotations em Ata, sem limite de tamanho).
/// </summary>
public class AcompanhamentoDtoValidatorTests
{
    private readonly AcompanhamentoDtoValidator _validator = new(new HtmlSanitizerService());

    private static AcompanhamentoDto DtoValido() => new()
    {
        DataReuniao = DateTime.Today,
        Ata = "Reunião de acompanhamento realizada normalmente."
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
    public void AtaVazia_DeveFalhar(string? ata)
    {
        var dto = DtoValido();
        dto.Ata = ata!;

        var result = _validator.Validate(dto);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(AcompanhamentoDto.Ata)
                                         && e.ErrorMessage == "O registro da ata é obrigatório.");
    }

    [Fact]
    public void AtaComExatamente4000Caracteres_DevePassar()
    {
        // Issue #73 — mesmo teto de Acompanhamento.Ata ([MaxLength(4000)]).
        var dto = DtoValido();
        dto.Ata = new string('a', 4000);

        var result = _validator.Validate(dto);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void AtaComMaisDe4000Caracteres_DeveFalhar()
    {
        var dto = DtoValido();
        dto.Ata = new string('a', 4001);

        var result = _validator.Validate(dto);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(AcompanhamentoDto.Ata)
                                         && e.ErrorMessage == "O registro da ata deve ter no máximo 4000 caracteres.");
    }

    [Fact]
    public void AtaDentroDoLimiteCru_MasQueExpandeAoSanitizar_DeveFalhar()
    {
        // Achado A10-1 da revisão de segurança — ver PropostaTccDtoValidatorTests para o
        // raciocínio completo (HtmlSanitizer codifica "&" em "&amp;", 5x maior).
        var dto = DtoValido();
        dto.Ata = new string('&', 4000);

        var result = _validator.Validate(dto);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(AcompanhamentoDto.Ata)
                                         && e.ErrorMessage == "O registro da ata deve ter no máximo 4000 caracteres.");
    }
}
