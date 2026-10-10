using FluentValidation;
using MimeKit;
using TccManager.Api.Services;

namespace TccManager.Api.Validators;

/// <summary>
/// Issue #155 (achados B4/B5): duas regras de validação estavam duplicadas verbatim entre
/// vários validators. Extraídas aqui como extensões sobre <c>IRuleBuilder</c>, mesmo espírito
/// de <c>UsuarioQueries</c>/<c>ControllerBaseExtensions</c> — uma única implementação para
/// cada regra, chamada a partir de cada validator.
/// </summary>
public static class FluentValidationExtensions
{
    /// <summary>
    /// Issue #73 (achado A10-1 da revisão de segurança): os controllers persistem
    /// <c>sanitizerService.Sanitizar(valor)</c>, não o valor cru — HtmlSanitizer só CODIFICA
    /// entidades (nunca decodifica), então validar o comprimento cru permite um valor dentro
    /// do limite estourar a coluna no INSERT. Medir o comprimento do valor JÁ sanitizado fecha
    /// esse descompasso. A mensagem de erro (específica de cada campo) continua a cargo do
    /// chamador via <c>.WithMessage(...)</c> encadeado.
    /// </summary>
    public static IRuleBuilderOptions<T, string> MaximoCaracteresSanitizados<T>(
        this IRuleBuilder<T, string> ruleBuilder, ISanitizerService sanitizerService, int max) =>
        ruleBuilder.Must(valor => (sanitizerService.Sanitizar(valor)?.Length ?? 0) <= max);

    /// <summary>
    /// Issue #99 (achado do QA do lote #70): <c>EmailAddress()</c> do FluentValidation é mais
    /// permissivo que o parser usado no envio (<c>MailboxAddress.Parse</c> em
    /// <c>MailKitEmailService</c>) — sem a primeira checagem <c>Must</c>, um e-mail passa na
    /// validação e só falha silenciosamente na hora de enviar. A segunda checagem <c>Must</c>
    /// rejeita a forma RFC de display-name (<c>"Nome &lt;email@x.com&gt;"</c>), que
    /// <c>MailboxAddress.TryParse</c> aceita mas nunca bate com o e-mail literal que o usuário
    /// de fato digitaria (login, ou o endereço de envio de um MembroExterno).
    /// </summary>
    public static IRuleBuilderOptions<T, string> EmailValidoParaEnvio<T>(this IRuleBuilder<T, string> ruleBuilder) =>
        ruleBuilder
            .NotEmpty().WithMessage("O email é obrigatório.")
            .EmailAddress().WithMessage("O email informado não é válido.")
            .MaximumLength(450).WithMessage("O email deve ter no máximo 450 caracteres.")
            .Must(email => MailboxAddress.TryParse(email, out _))
                .WithMessage("O email informado não é aceito pelo servidor de e-mail.")
            .Must(email => !MailboxAddress.TryParse(email, out var endereco) || endereco!.Address == email)
                .WithMessage("O email deve conter apenas o endereço, sem nome de exibição (ex.: \"nome@dominio.com\", não \"Nome <nome@dominio.com>\").");
}
