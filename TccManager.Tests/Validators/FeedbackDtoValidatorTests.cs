using TccManager.Api.Validators;
using TccManager.Shared.DTOs;
using Xunit;

namespace TccManager.Tests.Validators;

public class FeedbackDtoValidatorTests
{
    private const string MensagemEsperada = "A nota deve estar entre 0 e 10.";

    private readonly FeedbackDtoValidator _validator = new();

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
}
