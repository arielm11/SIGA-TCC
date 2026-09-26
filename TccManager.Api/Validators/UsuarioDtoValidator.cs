using FluentValidation;
using MimeKit;
using TccManager.Shared.DTOs;

namespace TccManager.Api.Validators;

public class UsuarioDtoValidator : AbstractValidator<UsuarioDto>
{
    public UsuarioDtoValidator()
    {
        RuleFor(dto => dto.Nome)
            .NotEmpty().WithMessage("O nome é obrigatório.")
            .MaximumLength(200).WithMessage("O nome deve ter no máximo 200 caracteres.");

        RuleFor(dto => dto.Email)
            .NotEmpty().WithMessage("O email é obrigatório.")
            .EmailAddress().WithMessage("O email informado não é válido.")
            .MaximumLength(450).WithMessage("O email deve ter no máximo 450 caracteres.")
            // EmailAddress() do FluentValidation é mais permissivo que o parser usado no
            // envio (MailboxAddress.Parse em MailKitEmailService) — sem esta checagem, um
            // e-mail passa na validação e só falha silenciosamente na hora de enviar
            // (ParseException, descartado com um warning). Mesmo parser nas duas pontas.
            .Must(email => MailboxAddress.TryParse(email, out _))
                .WithMessage("O email informado não é aceito pelo servidor de e-mail.")
            // Issue #99 (achado do QA do lote #70): MailboxAddress.TryParse aceita a forma RFC
            // de display-name ("Nome Exibido <email@x.com>") — sem esta checagem, esse valor
            // passa na validação e no índice único de e-mail (#65), mas o login (que compara
            // e-mail literal) nunca bate com o que o usuário de fato digitaria. Na prática, uma
            // forma de o próprio usuário se trancar pra fora da conta ao editar o e-mail com um
            // cliente/copy-paste que inclua o nome de exibição. Retorna true (defere pro Must
            // acima) quando o parse já falhou, para não duplicar mensagem de erro.
            .Must(email => !MailboxAddress.TryParse(email, out var endereco) || endereco!.Address == email)
                .WithMessage("O email deve conter apenas o endereço, sem nome de exibição (ex.: \"nome@dominio.com\", não \"Nome <nome@dominio.com>\").");
    }
}
