using TccManager.Api.Validators;
using TccManager.Shared.DTOs;
using Xunit;

namespace TccManager.Tests.Validators;

/// <summary>
/// Issue #73 (achado A07-1 da revisão de segurança) — <see cref="LoginDtoValidator"/> não
/// existia; LoginDto é o único DTO consumido por um endpoint [AllowAnonymous].
/// </summary>
public class LoginDtoValidatorTests
{
    private readonly LoginDtoValidator _validator = new();

    private static LoginDto DtoValido() => new()
    {
        Email = "usuario@teste.com",
        Senha = "senha-123"
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
    public void EmailVazio_DeveFalhar(string? email)
    {
        var dto = DtoValido();
        dto.Email = email!;

        var result = _validator.Validate(dto);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(LoginDto.Email)
                                         && e.ErrorMessage == "O email é obrigatório.");
    }

    [Fact]
    public void EmailComMaisDe450Caracteres_DeveFalhar()
    {
        var dto = DtoValido();
        dto.Email = new string('a', 445) + "@teste.com"; // 455 caracteres

        var result = _validator.Validate(dto);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(LoginDto.Email)
                                         && e.ErrorMessage == "O email deve ter no máximo 450 caracteres.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SenhaVazia_DeveFalhar(string? senha)
    {
        var dto = DtoValido();
        dto.Senha = senha!;

        var result = _validator.Validate(dto);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(LoginDto.Senha)
                                         && e.ErrorMessage == "A senha é obrigatória.");
    }

    [Fact]
    public void SenhaComMaisDe200Caracteres_DeveFalhar()
    {
        var dto = DtoValido();
        dto.Senha = new string('a', 201);

        var result = _validator.Validate(dto);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(LoginDto.Senha)
                                         && e.ErrorMessage == "A senha deve ter no máximo 200 caracteres.");
    }
}
