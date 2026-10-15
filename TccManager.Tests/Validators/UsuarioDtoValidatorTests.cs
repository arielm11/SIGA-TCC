using TccManager.Api.Validators;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using Xunit;

namespace TccManager.Tests.Validators;

/// <summary>
/// Cobertura direta e isolada de <see cref="UsuarioDtoValidator"/> — as regras de
/// Nome/Email/MaximumLength já são exercitadas indiretamente via
/// UsuarioController_EmailUnicoEUltimoAdmin_Tests.cs (integração HTTP); este arquivo cobre
/// especificamente o achado de segurança A05-1 (docs/seguranca/2026-08-18-fix-notificacoes-email-hardening.md):
/// EmailAddress() do FluentValidation é mais permissivo que o MailboxAddress.Parse usado de
/// fato no envio (MailKitEmailService) — sem o Must abaixo, esses e-mails passariam na
/// validação e só falhariam silenciosamente na hora de enviar uma notificação.
/// </summary>
public class UsuarioDtoValidatorTests
{
    private readonly UsuarioDtoValidator _validator = new();

    private static UsuarioDto DtoValido() => new()
    {
        Nome = "Usuario Teste",
        Email = "usuario@teste.com",
        Senha = "senha-123",
        Tipo = TipoUsuario.Aluno,
        Ativo = true
    };

    [Fact]
    public void DtoCompleto_DevePassar()
    {
        var result = _validator.Validate(DtoValido());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void NomeComExatamente200Caracteres_DevePassar()
    {
        // Issue #73 — mesmo teto de Usuario.Nome ([MaxLength(200)]).
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
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(UsuarioDto.Nome)
                                         && e.ErrorMessage == "O nome deve ter no máximo 200 caracteres.");
    }

    [Theory]
    [InlineData("a b@c.com")] // espaço no local-part: EmailAddress() aceita, MimeKit não
    [InlineData("<script>@x.com")]
    public void EmailAceitoPeloEmailAddressMasRejeitadoPeloParserDeEnvio_DeveFalhar(string email)
    {
        var dto = DtoValido();
        dto.Email = email;

        var result = _validator.Validate(dto);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(UsuarioDto.Email)
                                         && e.ErrorMessage == "O email informado não é aceito pelo servidor de e-mail.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmailNuloOuVazio_NaoLancaAoAvaliarORegraDeParserDeEnvio(string? email)
    {
        // CascadeMode padrão é Continue, então o Must(MailboxAddress.TryParse) roda mesmo
        // depois do NotEmpty já ter falhado — MailboxAddress.TryParse(null, ...) precisa
        // devolver false sem lançar, senão um e-mail vazio no PUT viraria 500 em vez de 400.
        var dto = DtoValido();
        dto.Email = email!;

        var result = _validator.Validate(dto);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(UsuarioDto.Email)
                                         && e.ErrorMessage == "O email é obrigatório.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Issue #99 (achado do QA do lote #70): MailboxAddress.TryParse aceita a forma RFC de
    // display-name — sem rejeitar isso, o login (que compara e-mail literal) nunca bateria
    // com o que o usuário de fato digitaria.
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Joao Silva <joao@teste.com>")]
    [InlineData("\"Joao Silva\" <joao@teste.com>")]
    public void EmailComFormaDeDisplayName_DeveFalhar(string email)
    {
        var dto = DtoValido();
        dto.Email = email;

        var result = _validator.Validate(dto);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(UsuarioDto.Email)
                                         && e.ErrorMessage == "O email deve conter apenas o endereço, sem nome de exibição (ex.: \"nome@dominio.com\", não \"Nome <nome@dominio.com>\").");
    }

    [Fact]
    public void EmailApenasOEndereco_SemDisplayName_DevePassar()
    {
        var dto = DtoValido();
        dto.Email = "joao@teste.com";

        var result = _validator.Validate(dto);

        Assert.True(result.IsValid);
    }
}
