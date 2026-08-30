using TccManager.Api.Services;
using TccManager.Api.Validators;
using TccManager.Shared.DTOs;
using Xunit;

namespace TccManager.Tests.Validators;

/// <summary>
/// Issue #73 — <see cref="RejeicaoDtoValidator"/> não existia (RejeicaoDto só tinha o
/// [Required] via DataAnnotations em Motivo, sem limite de tamanho).
///
/// Issue #81 (D5): o DTO ganhou um segundo dono (<c>OrientadorController.RejeitarEntrega</c>,
/// além de <c>CoordenadorController.RejeitarProposta</c>) e mudou de arquivo, mantendo o mesmo
/// namespace — nada muda aqui, porque estes testes exercitam o validador diretamente, sem
/// controller nem rota. A aplicação do validador na rota nova é coberta por
/// <c>OrientadorController_VeredictoEntrega_Tests</c> (motivo vazio e motivo que estoura o
/// limite ao ser sanitizado).
/// </summary>
public class RejeicaoDtoValidatorTests
{
    private readonly RejeicaoDtoValidator _validator = new(new HtmlSanitizerService());

    private static RejeicaoDto DtoValido() => new()
    {
        Motivo = "Fora do escopo do curso."
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
    public void MotivoVazio_DeveFalhar(string? motivo)
    {
        var dto = DtoValido();
        dto.Motivo = motivo!;

        var result = _validator.Validate(dto);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(RejeicaoDto.Motivo)
                                         && e.ErrorMessage == "O motivo da rejeição é obrigatório!");
    }

    [Fact]
    public void MotivoComExatamente2000Caracteres_DevePassar()
    {
        // Issue #73 — mesmo teto de Tcc.MotivoRejeicao ([MaxLength(2000)]).
        var dto = DtoValido();
        dto.Motivo = new string('a', 2000);

        var result = _validator.Validate(dto);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void MotivoComMaisDe2000Caracteres_DeveFalhar()
    {
        var dto = DtoValido();
        dto.Motivo = new string('a', 2001);

        var result = _validator.Validate(dto);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(RejeicaoDto.Motivo)
                                         && e.ErrorMessage == "O motivo da rejeição deve ter no máximo 2000 caracteres.");
    }

    [Fact]
    public void MotivoDentroDoLimiteCru_MasQueExpandeAoSanitizar_DeveFalhar()
    {
        // Achado A10-1 da revisão de segurança — ver PropostaTccDtoValidatorTests para o
        // raciocínio completo (HtmlSanitizer codifica "&" em "&amp;", 5x maior).
        var dto = DtoValido();
        dto.Motivo = new string('&', 2000);

        var result = _validator.Validate(dto);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(RejeicaoDto.Motivo)
                                         && e.ErrorMessage == "O motivo da rejeição deve ter no máximo 2000 caracteres.");
    }
}
