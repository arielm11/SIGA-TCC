using TccManager.Api.Services;
using TccManager.Api.Validators;
using TccManager.Shared.DTOs;
using Xunit;

namespace TccManager.Tests.Validators;

public class FeedbackDtoValidatorTests
{
    private const string MensagemEsperada = "A nota deve estar entre 0 e 10.";

    private readonly FeedbackDtoValidator _validator = new(new HtmlSanitizerService());

    private static FeedbackDto DtoComNota(decimal? nota) => new()
    {
        Feedback = "Bom trabalho.",
        Nota = nota
    };

    [Fact]
    public void NotaAusente_DevePassar()
    {
        // Nota é opcional (decimal?): quando null, nenhuma regra é aplicada.
        var result = _validator.Validate(DtoComNota(null));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(0)]   // limite inferior inclusivo
    [InlineData(5)]
    [InlineData(10)]  // limite superior inclusivo
    public void NotaDentroDoIntervalo_DevePassar(decimal nota)
    {
        var result = _validator.Validate(DtoComNota(nota));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    [InlineData(10.5)]
    public void NotaForaDoIntervalo_DeveFalhar_ComPropriedadeEMensagemCertas(decimal nota)
    {
        var result = _validator.Validate(DtoComNota(nota));

        Assert.False(result.IsValid);
        var erro = Assert.Single(result.Errors);
        Assert.Equal(nameof(FeedbackDto.Nota), erro.PropertyName);
        Assert.Equal(MensagemEsperada, erro.ErrorMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FeedbackVazio_DeveFalhar(string? feedback)
    {
        var dto = DtoComNota(8);
        dto.Feedback = feedback!;

        var result = _validator.Validate(dto);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(FeedbackDto.Feedback)
                                         && e.ErrorMessage == "O feedback é obrigatório.");
    }

    [Fact]
    public void FeedbackComExatamente2000Caracteres_DevePassar()
    {
        // Issue #73 — mesmo teto de Entrega.Feedback ([MaxLength(2000)]).
        var dto = DtoComNota(8);
        dto.Feedback = new string('a', 2000);

        var result = _validator.Validate(dto);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void FeedbackComMaisDe2000Caracteres_DeveFalhar()
    {
        var dto = DtoComNota(8);
        dto.Feedback = new string('a', 2001);

        var result = _validator.Validate(dto);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(FeedbackDto.Feedback)
                                         && e.ErrorMessage == "O feedback deve ter no máximo 2000 caracteres.");
    }

    [Fact]
    public void FeedbackDentroDoLimiteCru_MasQueExpandeAoSanitizar_DeveFalhar()
    {
        // Achado A10-1 da revisão de segurança — ver PropostaTccDtoValidatorTests para o
        // raciocínio completo (HtmlSanitizer codifica "&" em "&amp;", 5x maior).
        var dto = DtoComNota(8);
        dto.Feedback = new string('&', 2000);

        var result = _validator.Validate(dto);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(FeedbackDto.Feedback)
                                         && e.ErrorMessage == "O feedback deve ter no máximo 2000 caracteres.");
    }
}
