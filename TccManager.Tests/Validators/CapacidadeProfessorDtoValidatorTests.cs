using TccManager.Api.Validators;
using TccManager.Shared.DTOs;
using Xunit;

namespace TccManager.Tests.Validators;

public class CapacidadeProfessorDtoValidatorTests
{
    private const string MensagemEsperada = "O limite de orientandos deve estar entre 1 e 20.";

    private readonly CapacidadeProfessorDtoValidator _validator = new();

    private static CapacidadeProfessorDto DtoComLimite(int limite) => new()
    {
        LimiteOrientandos = limite,
        AceitandoOrientandos = true
    };

    [Theory]
    [InlineData(1)]   // limite inferior inclusivo
    [InlineData(10)]
    [InlineData(20)]  // limite superior inclusivo
    public void LimiteDentroDoIntervalo_DevePassar(int limite)
    {
        var result = _validator.Validate(DtoComLimite(limite));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(21)]
    public void LimiteForaDoIntervalo_DeveFalhar_ComPropriedadeEMensagemCertas(int limite)
    {
        var result = _validator.Validate(DtoComLimite(limite));

        Assert.False(result.IsValid);
        var erro = Assert.Single(result.Errors);
        Assert.Equal(nameof(CapacidadeProfessorDto.LimiteOrientandos), erro.PropertyName);
        Assert.Equal(MensagemEsperada, erro.ErrorMessage);
    }
}
